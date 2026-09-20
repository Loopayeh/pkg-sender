/* PKG Sender receiver - standalone PS5 payload.
 *
 * No etaHEN, no arsenal. Chain: jailbreak -> kstuff -> pkg-receiver.elf
 *
 * Listens on TCP 12800 (INADDR_ANY, LAN-reachable):
 *   GET  /              - WebUI: Library tab (PC catalog + covers) and
 *                          Manual URL tab; installs a home-screen launcher
 *                          ("pkg remote installer", PKGS12800) on startup
 *   GET  /api            - probe (open, no action)
 *   GET  /api/status     - {"busy":true/false,"active":N} install state
 *   GET  /api/pc         - {"pc":"1.2.3.4","age":N} last PC announce
 *                          (the PC broadcasts "PKGSENDER-PC ip:port" to UDP
 *                          12802 while Publish library is on; browsers can't
 *                          hear UDP, so we re-serve it here)
 *   GET  /install?url=   - install by query arg
 *   POST /api/install    - JSON {"packages":["<url>"]}
 *   POST /upload         - multipart form with a url field
 *   GET  /api/files/stat?path=            - file exists/size
 *   POST /api/files/mkdir  {"path":..}    - mkdir -p under /data/homebrew
 *   POST /api/files/write?path=..&offset= - raw chunk append
 *   POST /api/files/done   {"path":..,"size":N} - verify + toast
 *   UDP beacon: "PKGSENDER v1" broadcast to 255.255.255.255:12801 every 3s
 *
 * TEST_ONLY build (make TEST_ONLY=1): beacon + /api + /api/status work,
 * every install/file path is refused. Discovery testing only — nobody
 * can install anything with it.
 *
 * PKG install is sceAppInstUtilInstallByPackage, which accepts both
 * local paths (/data/xxx.pkg, mapped to /user/data/xxx.pkg) and remote
 * http:// URLs served from the PC (LAN install, no USB needed).
 * Other formats (exfat/ffpkg/ffpfsc/folders) are pushed as raw bytes
 * to /data/homebrew/ — mounting them afterwards is NOT our job.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>
#include <stdint.h>
#include <errno.h>
#include <fcntl.h>
#include <time.h>
#include <pthread.h>
#include <dlfcn.h>
#include <signal.h>
#include <sys/time.h>
#include <sys/stat.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/sysctl.h>
#include <netinet/in.h>
#include <arpa/inet.h>

#define DPI_PORT   12800
#define BEACON_PORT 12801
#define BEACON_MSG  "PKGSENDER v1"
#define HDR_MAX    16384
#define BODY_MAX   (8 * 1024 * 1024)
#define URL_MAX    2048
#define PATH_MAX_V 1024

/* ── PS5 system notification (visible toast on the console) ──────────── */
typedef struct notify_request {
	char unused[45];
	char message[3075];
} notify_request_t;

int sceKernelSendNotificationRequest(int device, notify_request_t *request,
                                     size_t size, int unused);

static void
notify_user(const char *msg)
{
	notify_request_t req;

	memset(&req, 0, sizeof(req));
	snprintf(req.message, sizeof(req.message), "%s", msg);
	sceKernelSendNotificationRequest(0, &req, sizeof(req), 0);
}

/* ── SCE AppInstUtil ABI (same layout websrv/ftpsrv use) ─────────────── */
typedef struct pkg_metadata {
	const char *uri;
	const char *ex_uri;
	const char *playgo_scenario_id;
	const char *content_id;
	const char *content_name;
	const char *icon_url;
	uint32_t slot;
	uint32_t is_playgo_enabled;
} pkg_metadata_t;

typedef struct pkg_info {
	char content_id[48];
	int type;
	int platform;
} pkg_info_t;

typedef struct playgo_info {
	char languages[30][8];
	char scenario_ids[64][3];
	char content_ids[64][48];
	long unknown[810];
} playgo_info_t;

/* AppInstUtil is resolved at runtime (dlopen), NOT linked: linking it
 * kills the payload at load time on some setups (proven on 6.02). */
typedef int (*init_fn)(void);
typedef int (*install_fn)(const pkg_metadata_t *, pkg_info_t *,
                           playgo_info_t *);

static pthread_mutex_t g_inst_lock = PTHREAD_MUTEX_INITIALIZER;
static int g_inst_ready = 0;
static void *g_ipmilib;
static void *g_applib;
static init_fn p_init;
static install_fn p_install;

/* 0 = ok, -1 = sprx not found, -2 = symbol not found, else SCE code */
static int
installer_init(void)
{
	int rc;

	pthread_mutex_lock(&g_inst_lock);
	if (g_inst_ready) {
		pthread_mutex_unlock(&g_inst_lock);
		return 0;
	}
	if (!g_applib) {
		/* AppInstUtil dies on load unless Ipmi is loaded first
		 * (proven on 6.02 with t_dl3). */
		if (!g_ipmilib) {
			g_ipmilib = dlopen("libSceIpmi.sprx", RTLD_LAZY);
			if (!g_ipmilib)
				g_ipmilib = dlopen(
				    "/system/common/lib/libSceIpmi.sprx",
				    RTLD_LAZY);
		}
		g_applib = dlopen("libSceAppInstUtil.sprx", RTLD_LAZY);
		if (!g_applib)
			g_applib = dlopen(
			    "/system/common/lib/libSceAppInstUtil.sprx",
			    RTLD_LAZY);
		if (!g_applib) {
			pthread_mutex_unlock(&g_inst_lock);
			return -1;
		}
	}
	if (!p_init) {
		p_init = (init_fn)dlsym(g_applib,
		    "sceAppInstUtilInitialize");
		if (!p_init) {
			pthread_mutex_unlock(&g_inst_lock);
			return -2;
		}
	}
	if (!p_install) {
		p_install = (install_fn)dlsym(g_applib,
		    "sceAppInstUtilInstallByPackage");
		if (!p_install) {
			pthread_mutex_unlock(&g_inst_lock);
			return -2;
		}
	}
	notify_user("Loopayeh: init AppInstUtil...");
	rc = p_init();
	if (rc == 0)
		g_inst_ready = 1;
	else
		notify_user("Loopayeh: AppInstUtil init failed");
	pthread_mutex_unlock(&g_inst_lock);
	return rc;
}

/* ── Home-screen launcher ("pkg remote installer", Media category) ────
 * Same idea as owendswang's ps5-web-file-manager (reimplemented here):
 * write /user/app/PKGS12800/sce_sys/{param.json,icon0.png} once, then
 * register the web shortcut via AppInstUtil. Best-effort: if the symbol
 * is missing the receiver keeps serving without a launcher.
 * Skipped entirely in TEST_ONLY builds. */
#ifndef TEST_ONLY
#define LAUNCHER_TID "PKGS12800"

__asm__(
".section .rodata\n"
".global launcher_param\n"
".global launcher_param_end\n"
".global launcher_param_size\n"
".align 16\n"
"launcher_param:\n"
".incbin \"launcher_param.json\"\n"
"launcher_param_end:\n"
"launcher_param_size:\n"
".quad launcher_param_end - launcher_param\n"
".previous\n");
extern const unsigned char launcher_param[];
extern const size_t launcher_param_size;

__asm__(
".section .rodata\n"
".global launcher_icon\n"
".global launcher_icon_end\n"
".global launcher_icon_size\n"
".align 16\n"
"launcher_icon:\n"
".incbin \"icon0.png\"\n"
"launcher_icon_end:\n"
"launcher_icon_size:\n"
".quad launcher_icon_end - launcher_icon\n"
".previous\n");
extern const unsigned char launcher_icon[];
extern const size_t launcher_icon_size;

typedef int (*titledir_fn)(const char *, const char *, void *);

static int
write_file_once(const char *path, const unsigned char *data, size_t size)
{
	struct stat st;
	FILE *f;

	if (stat(path, &st) == 0)
		return 0;
	if (errno != ENOENT)
		return -1;
	f = fopen(path, "wb");
	if (!f)
		return -1;
	if (fwrite(data, size, 1, f) != 1) {
		fclose(f);
		return -1;
	}
	fclose(f);
	return 0;
}

static void
launcher_install_if_needed(void)
{
	char dir[128], sdir[160], pj[192], ip[192];
	char toast[96];
	struct stat st;
	titledir_fn p_titledir;
	int rc;

	snprintf(dir, sizeof(dir), "/user/app/%s", LAUNCHER_TID);
	if (stat(dir, &st) == 0) {
		snprintf(pj, sizeof(pj), "%s/sce_sys/param.json", dir);
		snprintf(ip, sizeof(ip), "%s/sce_sys/icon0.png", dir);
		if (stat(pj, &st) == 0 && stat(ip, &st) == 0)
			return; /* already installed */
	}
	if (installer_init() != 0) {
		notify_user("Loopayeh: launcher skipped (AppInstUtil off)");
		return;
	}
	snprintf(sdir, sizeof(sdir), "%s/sce_sys", dir);
	if ((mkdir(dir, 0755) != 0 && errno != EEXIST) ||
	    (mkdir(sdir, 0755) != 0 && errno != EEXIST)) {
		notify_user("Loopayeh: launcher mkdir failed");
		return;
	}
	snprintf(pj, sizeof(pj), "%s/param.json", sdir);
	snprintf(ip, sizeof(ip), "%s/icon0.png", sdir);
	if (write_file_once(pj, launcher_param, launcher_param_size) != 0 ||
	    write_file_once(ip, launcher_icon, launcher_icon_size) != 0) {
		notify_user("Loopayeh: launcher file write failed");
		return;
	}
	p_titledir = (titledir_fn)dlsym(g_applib,
	    "sceAppInstUtilAppInstallTitleDir");
	if (!p_titledir) {
		notify_user("Loopayeh: launcher staged, registration N/A");
		return;
	}
	rc = p_titledir(LAUNCHER_TID, "/user/app/", NULL);
	if (rc == 0)
		notify_user("Loopayeh: home launcher installed");
	else {
		snprintf(toast, sizeof(toast),
		    "Loopayeh: launcher register 0x%08X", (unsigned)rc);
		notify_user(toast);
	}
}
#endif /* TEST_ONLY */

/* install runs on a detached worker so a slow/hanging SCE call can
 * never wedge the single-threaded HTTP loop. */
static const char *install_err_text(int rc, char *buf, size_t sz);
static int installer_install(const char *path, const char *want_name,
                             const char *want_icon,
                             char *name_out, size_t name_sz);
static void url_decode(const char *src, char *dst, size_t dst_sz);

typedef struct install_job {
	char url[URL_MAX];
	char name[256];
	char icon[512];
} install_job_t;

/* active install count for GET /api/status (multi-PKG queue pacing) */
static volatile int g_active_installs = 0;

/* last PC auto-announce (UDP 12802), re-served as GET /api/pc.
 * Defined with storage below; tentative here for handle_client. */
static char g_pc_addr[64];
static volatile time_t g_pc_seen;

static void *
install_worker(void *arg)
{
	install_job_t *job = arg;
	char name[256];
	char toast[256];
	char err[64];
	int rc;

	__sync_fetch_and_add(&g_active_installs, 1);
	rc = installer_install(job->url,
	    job->name[0] ? job->name : NULL,
	    job->icon[0] ? job->icon : NULL, name, sizeof(name));

	if (rc == 0)
		snprintf(toast, sizeof(toast), "Loopayeh: installing %s", name);
	else
		snprintf(toast, sizeof(toast), "Loopayeh: install failed %s",
		    install_err_text(rc, err, sizeof(err)));
	notify_user(toast);
	__sync_fetch_and_sub(&g_active_installs, 1);
	free(job);
	return NULL;
}

static int
queue_install(const char *url, const char *name, const char *icon)
{
#ifdef TEST_ONLY
	(void)url;
	(void)name;
	(void)icon;
	(void)install_worker; /* keep referenced so -Wunused-function stays quiet */
	/* defense in depth: the early refusal above should already have
	 * caught every install route. */
	return -1;
#else
	pthread_t tid;
	install_job_t *job;

	job = malloc(sizeof(*job));

	if (!job)
		return -1;
	snprintf(job->url, sizeof(job->url), "%s", url ? url : "");
	if (name)
		snprintf(job->name, sizeof(job->name), "%s", name);
	else
		job->name[0] = '\0';
	if (icon)
		snprintf(job->icon, sizeof(job->icon), "%s", icon);
	else
		job->icon[0] = '\0';
	if (pthread_create(&tid, NULL, install_worker, job) != 0) {
		free(job);
		return -1;
	}
	pthread_detach(tid);
	return 0;
#endif
}

/* returns 0 on success, SCE error code otherwise */
static int
installer_install(const char *path, const char *want_name,
                  const char *want_icon,
                  char *name_out, size_t name_sz)
{
	char local[URL_MAX + 32];
	const char *uri = path;
	const char *base;
	pkg_metadata_t meta;
	pkg_info_t info;
	playgo_info_t playgo;
	int rc;

	if (!path || !*path)
		return -1;

	if (!strncmp(path, "/data/", 6)) {
		snprintf(local, sizeof(local), "/user%s", path);
		uri = local;
	}

	base = strrchr(uri, '/');
	base = base ? base + 1 : uri;
	if (want_name && *want_name) {
		/* Sender told us the real game title: use it for the toast
		 * and for content_name shown by the console during install. */
		snprintf(name_out, name_sz, "%s", want_name);
	} else {
	/* strip query/fragment: "/proxy/?b64=.." -> basename would be garbage */
	{
		char tmp[256];
		size_t n = 0;

		while (base[n] && base[n] != '?' && base[n] != '#' &&
		       base[n] != '&' && n + 1 < sizeof(tmp)) {
			tmp[n] = base[n];
			n++;
		}
		tmp[n] = '\0';
		/* percent-decode the name too (tool url-encodes it) */
		url_decode(tmp, name_out, name_sz);
		if (!name_out[0])
			snprintf(name_out, name_sz, "%s", "PKG Sender Package");
	}
	}

	memset(&meta, 0, sizeof(meta));
	memset(&info, 0, sizeof(info));
	memset(&playgo, 0, sizeof(playgo));
	meta.uri = uri;
	meta.ex_uri = "";
	meta.playgo_scenario_id = "";
	meta.content_id = "";
	meta.content_name = name_out;
	/* Sender passes the cover URL it serves (/icon/..): the console
	 * fetches it itself for the download list. Empty = no cover. */
	meta.icon_url = (want_icon && *want_icon) ? want_icon : "";
	meta.slot = 0;
	meta.is_playgo_enabled = 0;

	rc = installer_init();
	if (rc)
		return rc;

	pthread_mutex_lock(&g_inst_lock);
	rc = p_install(&meta, &info, &playgo);
	pthread_mutex_unlock(&g_inst_lock);
	return rc;
}

/* ── tiny helpers ────────────────────────────────────────────────────── */
static int
send_all(int fd, const char *buf, size_t len)
{
	size_t off = 0;

	while (off < len) {
		ssize_t n = send(fd, buf + off, len - off, 0);
		if (n <= 0)
			return -1;
		off += (size_t)n;
	}
	return 0;
}

static void
send_text(int fd, const char *body)
{
	char hdr[256];
	int hlen = snprintf(hdr, sizeof(hdr),
	    "HTTP/1.0 200 OK\r\n"
	    "Content-Type: text/plain; charset=utf-8\r\n"
	    "Content-Length: %lu\r\n"
	    "Connection: close\r\n"
	    "\r\n", (unsigned long)strlen(body));

	send_all(fd, hdr, (size_t)hlen);
	send_all(fd, body, strlen(body));
}

static void
send_html(int fd, const char *body)
{
	char hdr[256];
	int hlen = snprintf(hdr, sizeof(hdr),
	    "HTTP/1.0 200 OK\r\n"
	    "Content-Type: text/html; charset=utf-8\r\n"
	    "Content-Length: %lu\r\n"
	    "Connection: close\r\n"
	    "\r\n", (unsigned long)strlen(body));

	send_all(fd, hdr, (size_t)hlen);
	send_all(fd, body, strlen(body));
}

static void
send_json(int fd, const char *body)
{
	char hdr[256];
	int hlen = snprintf(hdr, sizeof(hdr),
	    "HTTP/1.0 200 OK\r\n"
	    "Content-Type: application/json\r\n"
	    "Content-Length: %lu\r\n"
	    "Connection: close\r\n"
	    "\r\n", (unsigned long)strlen(body));

	send_all(fd, hdr, (size_t)hlen);
	send_all(fd, body, strlen(body));
}

/* %XX -> byte, + -> space. dst must fit URL_MAX. */
static void
url_decode(const char *src, char *dst, size_t dst_sz)
{
	size_t o = 0;

	while (*src && o + 1 < dst_sz) {
		if (*src == '%' && src[1] && src[2]) {
			char hex[3] = { src[1], src[2], 0 };
			dst[o++] = (char)strtol(hex, NULL, 16);
			src += 3;
		} else if (*src == '+') {
			dst[o++] = ' ';
			src++;
		} else {
			dst[o++] = *src++;
		}
	}
	dst[o] = '\0';
}

/* first http(s):// token in buf -> dst (stops at ws, quote, <, \r, \n). */
static int
grab_http_url(const char *buf, char *dst, size_t dst_sz)
{
	const char *p = strstr(buf, "http");
	size_t i = 0;

	if (!p)
		return 0;
	while (*p && i + 1 < dst_sz && *p != '"' && *p != '\'' &&
	       *p != '<' && *p != ' ' && *p != '\t' &&
	       *p != '\r' && *p != '\n')
		dst[i++] = *p++;
	dst[i] = '\0';
	return i > 0;
}

/* "packages" : [ " <url> " ] -> decoded url in dst. */
static int
json_first_package(const char *body, char *dst, size_t dst_sz)
{
	const char *p = strstr(body, "packages");
	char enc[URL_MAX];
	size_t i = 0;

	if (!p)
		return 0;
	p = strchr(p, '[');
	if (!p)
		return 0;
	p = strchr(p, '"');
	if (!p)
		return 0;
	p++;
	while (*p && *p != '"' && i + 1 < sizeof(enc))
		enc[i++] = *p++;
	enc[i] = '\0';
	if (i == 0)
		return 0;
	url_decode(enc, dst, dst_sz);
	return dst[0] != '\0';
}

static int
query_url(const char *path, char *dst, size_t dst_sz)
{
	const char *p = strstr(path, "url=");
	char raw[URL_MAX];
	size_t i = 0;

	if (!p)
		return 0;
	p += 4;
	while (*p && *p != '&' && *p != ' ' && i + 1 < sizeof(raw))
		raw[i++] = *p++;
	raw[i] = '\0';
	if (i == 0)
		return 0;
	url_decode(raw, dst, dst_sz);
	return dst[0] != '\0';
}

/* ── /data/homebrew file receiver ──────────────────────────────────── */
#define JAIL_PREFIX "/data/homebrew"

/* decoded absolute path must stay inside /data/homebrew */
static int
jail_path(const char *in, char *out, size_t sz)
{
	size_t pre = strlen(JAIL_PREFIX);

	if (!in || strlen(in) + 1 > sz)
		return -1;
	if (strcmp(in, JAIL_PREFIX) != 0 && strncmp(in, JAIL_PREFIX "/", pre + 1) != 0)
		return -1;
	if (strstr(in, ".."))
		return -1;
	strcpy(out, in);
	return 0;
}

static int
mkdir_p(const char *path)
{
	char tmp[PATH_MAX_V];
	size_t i, n = strlen(path);

	if (n == 0 || n >= sizeof(tmp))
		return -1;
	strcpy(tmp, path);
	for (i = 1; i < n; i++) {
		if (tmp[i] == '/') {
			tmp[i] = '\0';
			if (mkdir(tmp, 0755) != 0 && errno != EEXIST)
				return -1;
			tmp[i] = '/';
		}
	}
	if (mkdir(tmp, 0755) != 0 && errno != EEXIST)
		return -1;
	return 0;
}

/* query key= -> decoded value (stops at & or space) */
static int
query_param(const char *path, const char *key, char *dst, size_t dst_sz)
{
	char pat[64], raw[PATH_MAX_V];
	size_t i = 0;
	const char *p;

	snprintf(pat, sizeof(pat), "%s=", key);
	p = strstr(path, pat);
	if (!p)
		return 0;
	p += strlen(pat);
	while (*p && *p != '&' && *p != ' ' && i + 1 < sizeof(raw))
		raw[i++] = *p++;
	raw[i] = '\0';
	if (i == 0)
		return 0;
	url_decode(raw, dst, dst_sz);
	return dst[0] != '\0';
}

/* "key" : "string" (handles \" and \\) -> dst */
static int
json_string(const char *body, const char *key, char *dst, size_t dst_sz)
{
	char pat[64];
	const char *p;
	size_t o = 0;

	snprintf(pat, sizeof(pat), "\"%s\"", key);
	p = strstr(body, pat);
	if (!p)
		return 0;
	p = strchr(p + strlen(pat), ':');
	if (!p)
		return 0;
	p = strchr(p, '"');
	if (!p)
		return 0;
	p++;
	while (*p && *p != '"' && o + 1 < dst_sz) {
		if (*p == '\\' && (p[1] == '"' || p[1] == '\\')) {
			dst[o++] = p[1];
			p += 2;
		} else {
			dst[o++] = *p++;
		}
	}
	dst[o] = '\0';
	return o > 0;
}

/* "key" : 12345 -> value */
static int
json_long(const char *body, const char *key, long long *out)
{
	char pat[64];
	const char *p;

	snprintf(pat, sizeof(pat), "\"%s\"", key);
	p = strstr(body, pat);
	if (!p)
		return 0;
	p = strchr(p + strlen(pat), ':');
	if (!p)
		return 0;
	p++;
	while (*p == ' ' || *p == '\t')
		p++;
	*out = strtoll(p, NULL, 10);
	return 1;
}

/* (send_json is defined above with the other reply helpers) */

/* read until end of HTTP headers. returns header length or -1. */
static long
read_headers(int fd, char *buf, size_t cap)
{
	size_t total = 0;

	while (total + 1 < cap) {
		ssize_t n = recv(fd, buf + total, cap - 1 - total, 0);
		if (n <= 0)
			return -1;
		total += (size_t)n;
		buf[total] = '\0';
		if (strstr(buf, "\r\n\r\n"))
			return (long)total;
	}
	return -1;
}

static long
content_length(const char *hdr)
{
	const char *p = strcasestr(hdr, "content-length:");

	if (!p)
		return 0;
	return strtol(p + 15, NULL, 10);
}

static const char UI_HTML[] =
#ifdef TEST_ONLY
"<!DOCTYPE html><html><head><meta charset=utf-8>"
"<title>PKG Sender (test build)</title></head>"
"<body style='background:#101418;color:#eee;font-family:sans-serif;"
"display:flex;align-items:center;justify-content:center;min-height:100vh'>"
"<h2>PKG Sender receiver — TEST BUILD, installs disabled</h2>"
"</body></html>";
#else
"<!DOCTYPE html><html><head><meta charset=utf-8>"
"<meta name=viewport content='width=device-width,initial-scale=1'>"
"<title>pkg remote installer</title>"
"<style>body{background:#101418;color:#eee;font-family:sans-serif;margin:0;padding:16px}"
"h2{color:#7fd4ff;margin:0 0 12px}#tabs{display:flex;gap:8px;margin-bottom:14px}"
"#tabs button{flex:1;padding:12px;background:#1c232c;border:1px solid #7fd4ff;border-radius:6px;color:#7fd4ff;font-size:16px;font-weight:bold;cursor:pointer}"
"#tabs button.on{background:#7fd4ff;color:#101418}"
"#pcrow{display:flex;gap:8px;margin-bottom:12px;align-items:center}"
"#pcstat{font-size:13px;color:#8B93A5;white-space:nowrap}"
"#tools{display:flex;gap:8px;margin-bottom:12px}"
"#tools input{flex:1}"
"#chips{display:flex;gap:6px}"
"#chips button{padding:12px 14px;background:#1c232c;border:1px solid #7fd4ff;border-radius:6px;color:#7fd4ff;font-size:14px;cursor:pointer}"
"#chips button.on{background:#7fd4ff;color:#101418}"
"#cfg{display:flex;gap:8px;margin-bottom:12px}"
"input{flex:1;padding:12px;border:1px solid #7fd4ff;border-radius:6px;background:#0b0e12;color:#eee;font-size:15px}"
"button.go{padding:12px;background:#7fd4ff;border:none;border-radius:6px;color:#101418;font-size:15px;font-weight:bold;cursor:pointer}"
"#grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}"
".card{background:#1c232c;border-radius:8px;padding:10px;text-align:center}"
".card img{width:100%;height:120px;object-fit:contain;background:#0b0e12;border-radius:6px}"
".card .t{font-size:13px;margin:8px 0 2px;min-height:32px}"
".card .m{font-size:11px;color:#8B93A5;margin-bottom:8px}"
".card button{width:100%;padding:10px;background:#7fd4ff;border:none;border-radius:6px;color:#101418;font-weight:bold;cursor:pointer}"
".fc{display:inline-block;font-size:11px;color:#7fd4ff;border:1px solid #7fd4ff;border-radius:10px;padding:2px 8px;margin-top:4px}"
".fambox{margin-top:8px;display:flex;flex-direction:column;gap:6px}"
".member{display:flex;gap:8px;align-items:center;background:#0b0e12;border-radius:6px;padding:8px;text-align:left}"
".member .t{font-size:12px}.member .m{font-size:11px;color:#8B93A5}"
".member div:first-child{flex:1}"
".rb{font-size:10px;color:#101418;background:#7fd4ff;border-radius:4px;padding:2px 6px;margin-right:6px}"
".member button{padding:8px 12px;background:#7fd4ff;border:none;border-radius:6px;color:#101418;font-weight:bold;cursor:pointer}"
"#msg{margin-top:14px;font-size:14px;min-height:20px}"
"form#mf{display:flex;flex-direction:column;gap:12px;width:90%;max-width:520px;margin:0 auto}</style></head><body>"
"<h2>pkg remote installer</h2>"
"<div id=tabs><button id=tabL class=on>Library</button><button id=tabM>Manual URL</button></div>"
"<div id=lib>"
"<div id=pcrow><span id=pcstat>PC: ...</span>"
"<input id=pc placeholder='PC address'><button class=go id=save>Save</button></div>"
"<div id=tools><input id=q placeholder='Search title or ID...'>"
"<div id=chips><button data-p=all class=on>All</button><button data-p=PS5>PS5</button><button data-p=PS4>PS4</button></div></div>"
"<div id=grid></div><div id=msg></div></div>"
"<div id=man style='display:none'><form id=mf><input id=url type=url placeholder='http://192.168.x.x:9898/game.pkg' required>"
"<button class=go type=submit>Install PKG</button></form><div id=st style='text-align:center'></div></div>"
"<script>(function(){var pcEl=document.getElementById('pc');"
"var pcstat=document.getElementById('pcstat');"
"var grid=document.getElementById('grid');var msg=document.getElementById('msg');"
"var qEl=document.getElementById('q');"
"var all=[],openFam=null,plat='all';"
"pcEl.value=localStorage.getItem('pri_pc')||'';"
"function show(which){document.getElementById('lib').style.display=which?'':'none';"
"document.getElementById('man').style.display=which?'none':'';"
"document.getElementById('tabL').className=which?'on':'';"
"document.getElementById('tabM').className=which?'':'on';}"
"document.getElementById('tabL').onclick=function(){show(1);};"
"document.getElementById('tabM').onclick=function(){show(0);};"
"function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;');}"
"function rank(r){return r==='Patch'?1:(r==='DLC'?2:0);}"
"function lone(g){return (g.familyKey||'').indexOf('FILE:')===0;}"
"function isBase(g){return g.role==='Game'||lone(g);}"
"function matchQ(g){var q=qEl.value.trim().toLowerCase();if(!q)return 1;"
"return (g.title+' '+g.titleId).toLowerCase().indexOf(q)>=0;}"
"function matchP(g){if(plat==='all')return 1;return g.platform===plat;}"
"async function install(id,name){msg.textContent='Installing '+name+'...';"
"try{var u='http://'+pcEl.value+':9898/pkg/'+encodeURIComponent(id);"
"var r=await fetch('/install?url='+encodeURIComponent(u));"
"msg.textContent=await r.text();}catch(ex){msg.textContent='Error: '+ex;}}"
"function card(g,sub){var d=document.createElement('div');d.className=sub?'member':'card';"
"var im=(!sub&&g.hasIcon)?'<img src=\"http://'+pcEl.value+':9898/icon/'+encodeURIComponent(g.id)+'\">':'';"
"var meta=esc(g.titleId||'');if(g.version)meta+=' v'+esc(g.version);"
"if(g.sizeText)meta+=' &middot; '+esc(g.sizeText);"
"var badge=sub?'<span class=rb>'+esc(g.role)+'</span>':'';"
"if(sub){d.innerHTML='<div><div class=t>'+esc(g.title)+'</div><div class=m>'+meta+'</div></div>';"
"var w=document.createElement('div');w.innerHTML=badge;"
"var b=document.createElement('button');b.textContent='Install';"
"b.onclick=function(ev){ev.stopPropagation();install(g.id,g.title);};"
"w.appendChild(b);d.appendChild(w);return d;}"
"var fam=all.filter(function(m){return !isBase(m)&&m.familyKey===g.familyKey;});"
"fam.sort(function(a,b){return rank(a.role)-rank(b.role);});"
"var cnt=fam.length?'<span class=fc>'+fam.length+' add-on'+(fam.length>1?'s':'')+'</span>':'';"
"d.innerHTML=im+'<div class=t>'+esc(g.title)+'</div><div class=m>'+meta+'</div>'+cnt;"
"var ib=document.createElement('button');ib.textContent='Install';"
"ib.onclick=function(ev){ev.stopPropagation();install(g.id,g.title);};d.appendChild(ib);"
"d.onclick=function(){openFam=(openFam===g.familyKey)?null:g.familyKey;render();};"
"if(openFam===g.familyKey&&fam.length){var box=document.createElement('div');box.className='fambox';"
"fam.forEach(function(m){box.appendChild(card(m,1));});"
"var wrap=document.createElement('div');wrap.appendChild(d);wrap.appendChild(box);return wrap;}"
"return d;}"
"function render(){grid.innerHTML='';"
"var bases=all.filter(function(g){return isBase(g)&&matchP(g)&&matchQ(g);});"
"bases.sort(function(a,b){return a.title.toLowerCase()<b.title.toLowerCase()?-1:1;});"
"if(!bases.length){msg.textContent=all.length?'No match.':'Library is empty — tick Publish library in PKG Sender.';return;}"
"msg.textContent=bases.length+' games';"
"bases.forEach(function(g){grid.appendChild(card(g,0));});}"
"async function resolvePc(){"
"try{var r=await fetch('/api/pc');var j=await r.json();"
"if(j.pc&&j.age>=0&&j.age<15){pcEl.value=j.pc;pcstat.textContent='PC: '+j.pc+' (auto)';return j.pc;}}catch(e){}"
"var m=(pcEl.value||localStorage.getItem('pri_pc')||'').trim();"
"if(m){pcEl.value=m;pcstat.textContent='PC: '+m+' (manual)';return m;}"
"pcstat.textContent='PC: ?';return '';}"
"async function load(){var pc=await resolvePc();"
"if(!pc){msg.textContent='No PC found — tick Publish library in PKG Sender, or type the PC address.';return;}"
"localStorage.setItem('pri_pc',pc);msg.textContent='Loading...';grid.innerHTML='';all=[];"
"try{var r=await fetch('http://'+pc+':9898/catalog');"
"all=await r.json();openFam=null;render();}"
"catch(ex){msg.textContent='Error: '+ex+' — is Publish library on and the PC reachable?';}}"
"document.getElementById('save').onclick=load;"
"qEl.oninput=render;"
"var chips=document.getElementById('chips').children;"
"for(var i=0;i<chips.length;i++)(function(c){c.onclick=function(){plat=c.getAttribute('data-p');"
"for(var k=0;k<chips.length;k++)chips[k].className='';c.className='on';render();};})(chips[i]);"
"document.getElementById('mf').onsubmit=async function(e){e.preventDefault();"
"var u=document.getElementById('url').value;"
"document.getElementById('st').textContent='Installing...';"
"try{var r=await fetch('/install?url='+encodeURIComponent(u));"
"var x=await r.text();document.getElementById('st').textContent=x;}"
"catch(ex){document.getElementById('st').textContent='Error: '+ex;}};"
"load();})();</script>"
"</body></html>";
#endif

/* human text for install errors (-1/-2 are ours, rest are SCE codes) */
static const char *
install_err_text(int rc, char *buf, size_t sz)
{
	if (rc == -1)
		snprintf(buf, sz, "AppInstUtil sprx not found");
	else if (rc == -2)
		snprintf(buf, sz, "AppInstUtil symbols not found");
	else
		snprintf(buf, sz, "0x%08X", (unsigned)rc);
	return buf;
}

static void
do_install_reply_text(int fd, const char *url, const char *name,
                      const char *icon)
{
	char disp[256], out[URL_MAX + 64];

	if (name && *name)
		snprintf(disp, sizeof(disp), "%s", name);
	else {
		const char *base = strrchr(url, '/');
		base = base ? base + 1 : url;
		snprintf(disp, sizeof(disp), "%s", base);
	}
	if (queue_install(url, name, icon) == 0)
		snprintf(out, sizeof(out), "ok: install queued for %s", disp);
	else
		snprintf(out, sizeof(out), "error:queue failed");
	send_text(fd, out);
}

static void
handle_client(int fd)
{
	char *buf = malloc(HDR_MAX + BODY_MAX + 1);
	char method[16], path[URL_MAX + 64];
	long hdr_len, body_len;
	char *body;
	char url[URL_MAX];
	struct timeval tv;

	if (!buf) {
		close(fd);
		return;
	}

	/* a silent connection must never wedge the single-threaded loop */
	tv.tv_sec = 10;
	tv.tv_usec = 0;
	setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
	setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));

	hdr_len = read_headers(fd, buf, HDR_MAX);
	if (hdr_len < 0) {
		free(buf);
		close(fd);
		return;
	}

	if (sscanf(buf, "%15s %2079s", method, path) != 2) {
		free(buf);
		close(fd);
		return;
	}

#ifdef TEST_ONLY
	/* test build: probes + beacon stay alive, everything that can
	 * install or write files is refused up front. */
	if (!strcmp(method, "POST") ||
	    !strncmp(path, "/install", 8) ||
	    !strncmp(path, "/api/files/", 11)) {
		send_text(fd, "Loopayeh: test build, installs disabled");
		goto handled;
	}
#endif

	body_len = content_length(buf);
	if (body_len < 0 || body_len > BODY_MAX) {
		send_text(fd, "Loopayeh: bad content length");
		free(buf);
		close(fd);
		return;
	}
	/* HttpClient/curl wait for this before sending a POST body */
	if (strcasestr(buf, "expect: 100-continue"))
		send_all(fd, "HTTP/1.1 100 Continue\r\n\r\n", 25);
	body = buf + hdr_len;
	/* header terminator "\r\n\r\n" is 4 bytes; body starts after it */
	{
		char *end = strstr(buf, "\r\n\r\n");
		if (end)
			body = end + 4;
	}
	if (body_len > 0) {
		/* bytes after headers may already be in buf */
		long buffered = hdr_len - (body - buf);
		long missing = body_len - buffered;
		while (missing > 0) {
			ssize_t n = recv(fd, body + buffered,
			    (size_t)missing, 0);
			if (n <= 0)
				break;
			buffered += n;
			missing -= n;
		}
		if ((size_t)(body - buf) + (size_t)body_len >=
		    HDR_MAX + BODY_MAX)
			body_len = buffered;
		else
			body[body_len] = '\0';
	} else {
		*body = '\0';
	}

	if (!strcmp(method, "GET") && !strcmp(path, "/api")) {
		/* open probe: identifies us, performs nothing */
		send_json(fd,
		    "{\"status\":\"fail\","
		    "\"error\":\"Unsupported method: use POST /api/install\"}");
	} else if (!strcmp(method, "GET") &&
	           (!strncmp(path, "/install", 8))) {
		char gname[256];
		char gicon[512];

		if (query_url(path, url, sizeof(url))) {
			gname[0] = '\0';
			gicon[0] = '\0';
			query_param(path, "name", gname, sizeof(gname));
			query_param(path, "icon", gicon, sizeof(gicon));
			do_install_reply_text(fd, url, gname[0] ? gname : NULL,
			                      gicon[0] ? gicon : NULL);
		} else {
			send_text(fd, "error:missing url");
		}
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/files/stat", 15)) {
		char rpath[URL_MAX], local[PATH_MAX_V];
		struct stat st;

		if (!query_param(path, "path", rpath, sizeof(rpath)) ||
		    jail_path(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad path");
		} else if (stat(local, &st) == 0) {
			char out[128];
			long long sz = S_ISDIR(st.st_mode) ? 0 : (long long)st.st_size;
			snprintf(out, sizeof(out),
			    "{\"exists\":true,\"size\":%lld}", sz);
			send_json(fd, out);
		} else {
			send_json(fd, "{\"exists\":false,\"size\":0}");
		}
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/status", 11)) {
		char out[64];

		snprintf(out, sizeof(out), "{\"busy\":%s,\"active\":%d}",
		    g_active_installs > 0 ? "true" : "false",
		    g_active_installs);
		send_json(fd, out);
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/pc", 7)) {
		char out[128];
		time_t now = time(NULL);
		long age = g_pc_seen > 0 ? (long)(now - g_pc_seen) : -1;

		snprintf(out, sizeof(out), "{\"pc\":\"%s\",\"age\":%ld}",
		    g_pc_addr, age);
		send_json(fd, out);
	} else if (!strcmp(method, "GET")) {
		send_html(fd, UI_HTML);
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/install", 12)) {
		char gname[256];
		char gicon[512];

		if (json_first_package(body, url, sizeof(url))) {
			gname[0] = '\0';
			gicon[0] = '\0';
			json_string(body, "name", gname, sizeof(gname));
			json_string(body, "icon_url", gicon, sizeof(gicon));
			if (queue_install(url, gname[0] ? gname : NULL,
			                  gicon[0] ? gicon : NULL) == 0)
				send_json(fd, "{\"status\":\"success\"}");
			else
				send_json(fd,
				    "{\"status\":\"fail\","
				    "\"error\":\"queue failed\"}");
		} else {
			send_json(fd,
			    "{\"status\":\"fail\","
			    "\"error\":\"no package url\"}");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/upload", 7)) {
		if (grab_http_url(body, url, sizeof(url))) {
			char out[URL_MAX + 32];

			if (queue_install(url, NULL, NULL) == 0)
				snprintf(out, sizeof(out),
				    "SUCCESS: %s", url);
			else
				snprintf(out, sizeof(out),
				    "FAILED: queue failed");
			send_text(fd, out);
		} else {
			send_text(fd, "FAILED: no url field");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/files/mkdir", 16)) {
		char rpath[PATH_MAX_V], local[PATH_MAX_V];

		if (!json_string(body, "path", rpath, sizeof(rpath)) ||
		    jail_path(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad path");
		} else if (mkdir_p(local) == 0) {
			send_json(fd, "{\"ok\":true}");
		} else {
			send_text(fd, "error:mkdir failed");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/files/write", 16)) {
		char rpath[URL_MAX], local[PATH_MAX_V];
		char offs[32];
		long long off;
		int wfd;
		char *wp;
		long left;

		if (!query_param(path, "path", rpath, sizeof(rpath)) ||
		    jail_path(rpath, local, sizeof(local)) != 0 ||
		    !query_param(path, "offset", offs, sizeof(offs))) {
			send_text(fd, "error:bad path/offset");
		} else {
			off = strtoll(offs, NULL, 10);
			if (off < 0) {
				send_text(fd, "error:bad offset");
				goto handled;
			}
			wfd = open(local, O_WRONLY | O_CREAT, 0644);
			if (wfd < 0) {
				send_text(fd, "error:open failed");
				goto handled;
			}
			if (lseek(wfd, (off_t)off, SEEK_SET) == (off_t)-1) {
				close(wfd);
				send_text(fd, "error:seek failed");
				goto handled;
			}
			wp = body;
			left = body_len;
			while (left > 0) {
				ssize_t n = write(wfd, wp, (size_t)left);
				if (n <= 0) {
					if (errno == EINTR)
						continue;
					break;
				}
				wp += n;
				left -= n;
			}
			close(wfd);
			if (left != 0)
				send_text(fd, "error:write failed");
			else
				send_json(fd, "{\"ok\":true}");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/files/done", 15)) {
		char rpath[PATH_MAX_V], local[PATH_MAX_V];
		long long want;
		struct stat st;

		if (!json_string(body, "path", rpath, sizeof(rpath)) ||
		    jail_path(rpath, local, sizeof(local)) != 0 ||
		    !json_long(body, "size", &want)) {
			send_text(fd, "error:bad path/size");
		} else if (stat(local, &st) != 0 ||
		           (!S_ISDIR(st.st_mode) && (long long)st.st_size != want)) {
			send_text(fd, "error:size mismatch");
		} else {
			char out[160], toast[160], base[128];
			const char *b = strrchr(local, '/');
			snprintf(base, sizeof(base), "%s", b ? b + 1 : local);
			snprintf(out, sizeof(out),
			    "{\"ok\":true,\"size\":%lld}", (long long)st.st_size);
			send_json(fd, out);
			snprintf(toast, sizeof(toast),
			    "Loopayeh: received %s", base);
			notify_user(toast);
		}
	} else {
		send_text(fd, "Loopayeh: unknown endpoint");
	}
handled:

	free(buf);
	close(fd);
}

/* ── UDP discovery beacon ────────────────────────────────────────────
 * Every 3s broadcast "PKGSENDER v1" to 255.255.255.255:12801 so the PC
 * sender finds the console without a subnet sweep. Fire-and-forget:
 * beacon failure never affects installs. Sender IP = packet source. */
static void *
beacon_worker(void *arg)
{
	(void)arg;
	int fd = socket(AF_INET, SOCK_DGRAM, 0);
	struct sockaddr_in bc;
	int one = 1;

	if (fd < 0)
		return NULL;
	setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &one, sizeof(one));
	memset(&bc, 0, sizeof(bc));
	bc.sin_family = AF_INET;
	bc.sin_addr.s_addr = htonl(INADDR_BROADCAST);
	bc.sin_port = htons(BEACON_PORT);
	for (;;) {
		sendto(fd, BEACON_MSG, strlen(BEACON_MSG), 0,
		    (struct sockaddr *)&bc, sizeof(bc));
		sleep(3);
	}
	return NULL;
}

static void
beacon_start(void)
{
	pthread_t tid;

	if (pthread_create(&tid, NULL, beacon_worker, NULL) == 0)
		pthread_detach(tid);
}

/* ── PC auto-announce listener ─────────────────────────────────────────
 * While Publish library is on, the PC broadcasts "PKGSENDER-PC ip:port"
 * to UDP 12802 every 3s. Browsers can't hear UDP, so we listen here and
 * re-serve the last announcement to the Library page over HTTP
 * (GET /api/pc). */
#define PC_ANNOUNCE_PORT 12802
#define PC_ANNOUNCE_MAGIC "PKGSENDER-PC "

static char g_pc_addr[64] = "";
static volatile time_t g_pc_seen = 0;

static void *
pc_listen_worker(void *arg)
{
	(void)arg;
	int fd = socket(AF_INET, SOCK_DGRAM, 0);
	struct sockaddr_in sa, from;
	socklen_t fl;
	char buf[128];
	ssize_t n;

	if (fd < 0)
		return NULL;
	memset(&sa, 0, sizeof(sa));
	sa.sin_family = AF_INET;
	sa.sin_addr.s_addr = htonl(INADDR_ANY);
	sa.sin_port = htons(PC_ANNOUNCE_PORT);
	if (bind(fd, (struct sockaddr *)&sa, sizeof(sa)) != 0) {
		close(fd);
		return NULL;
	}
	for (;;) {
		fl = sizeof(from);
		n = recvfrom(fd, buf, sizeof(buf) - 1, 0,
		    (struct sockaddr *)&from, &fl);
		if (n <= 0)
			continue;
		buf[n] = '\0';
		if (strncmp(buf, PC_ANNOUNCE_MAGIC,
		    sizeof(PC_ANNOUNCE_MAGIC) - 1) != 0)
			continue;
		if (from.sin_family != AF_INET)
			continue;
		if (!inet_ntop(AF_INET, &from.sin_addr,
		    g_pc_addr, sizeof(g_pc_addr)))
			continue;
		g_pc_seen = time(NULL);
	}
	return NULL;
}

static void
pc_listen_start(void)
{
	pthread_t tid;

	if (pthread_create(&tid, NULL, pc_listen_worker, NULL) == 0)
		pthread_detach(tid);
}

/* ── Process identity + self-replacement ───────────────────────────────
 * Name our main thread so process managers (e.g. itsPLK's
 * ps5-payload-manager, which lists ki_comm/ki_tdname) show us as
 * pkg-receiver.elf, and kill any previous instance on startup so
 * re-injecting just works without a console reboot. */
#define RECEIVER_NAME "pkg-receiver.elf"

/* pid of another live process whose thread name matches ours, else -1 */
static pid_t
find_receiver_peer(void)
{
	int mib[4] = { 1, 14, 8, 0 };
	pid_t self = getpid();
	pid_t found = -1;
	size_t len = 0;
	uint8_t *buf, *p, *end;

	if (sysctl(mib, 4, NULL, &len, NULL, 0) != 0 || len == 0)
		return -1;
	buf = malloc(len);
	if (!buf)
		return -1;
	if (sysctl(mib, 4, buf, &len, NULL, 0) != 0) {
		free(buf);
		return -1;
	}
	end = buf + len;
	for (p = buf; p + (int)sizeof(int) <= end;) {
		int sz = *(int *)p;
		pid_t pid;
		if (sz < 468 || p + sz > end)
			break;
		pid = *(pid_t *)(p + 72);
		if (pid != self && pid > 0 &&
		    strncmp((char *)(p + 447), RECEIVER_NAME,
		        sizeof(RECEIVER_NAME)) == 0)
			found = pid;
		p += sz;
	}
	free(buf);
	return found;
}

int
main(void)
{
	int srv, cl;
	int opt = 1;
	struct sockaddr_in sa;

	notify_user("Loopayeh: stage main entered");

	syscall(SYS_thr_set_name, -1, RECEIVER_NAME);

	/* replace any previous instance: re-inject needs no reboot */
	for (;;) {
		pid_t old = find_receiver_peer();
		if (old <= 0)
			break;
		if (kill(old, SIGKILL) != 0)
			break;
		sleep(1);
	}

	srv = socket(AF_INET, SOCK_STREAM, 0);
	notify_user("Loopayeh: stage socket done");
	if (srv < 0) {
		notify_user("Loopayeh: socket failed, exiting");
		return 1;
	}
	setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

	memset(&sa, 0, sizeof(sa));
	sa.sin_family = AF_INET;
	sa.sin_addr.s_addr = htonl(INADDR_ANY);
	sa.sin_port = htons(DPI_PORT);

	if (bind(srv, (struct sockaddr *)&sa, sizeof(sa)) < 0) {
		notify_user("Loopayeh: port 12800 busy, exiting");
		return 1;
	}
	if (listen(srv, 8) < 0) {
		notify_user("Loopayeh: listen failed, exiting");
		return 1;
	}

	notify_user(
#ifdef TEST_ONLY
	    "Loopayeh: TEST BUILD listening (no installs)"
#else
	    "Loopayeh: listening on port 12800"
#endif
	    );

	beacon_start();
	pc_listen_start();

#ifndef TEST_ONLY
	launcher_install_if_needed();
#endif

	for (;;) {
		cl = accept(srv, NULL, NULL);
		if (cl < 0)
			continue;
		handle_client(cl);
	}

	return 0;
}
