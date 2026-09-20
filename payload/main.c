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
#include <dirent.h>
#include <netdb.h>
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
/* bump on every behavior change; the page shows receiver vs page tags */
#define RECEIVER_BUILD "20260920-08"

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

/* last raw request line, for /api/dbg */
static char g_last_req[256] = "";
/* last PC auto-announce (UDP 12802), re-served as GET /api/pc. */
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
	    "Cache-Control: no-store\r\n"
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

/* ── /data file browser (Files tab) ────────────────────────────────────
 * All paths stay inside FS_ROOT (/data); no ".." escapes; delete is
 * files + empty dirs only (no recursive delete). */
#define FS_ROOT "/data"
#define FS_MAX_ENTRIES 2000

static int
fs_jail(const char *in, char *out, size_t sz)
{
	size_t pre = strlen(FS_ROOT);

	if (!in || strlen(in) + 1 > sz)
		return -1;
	if (strcmp(in, FS_ROOT) != 0 && strncmp(in, FS_ROOT "/", pre + 1) != 0)
		return -1;
	if (strstr(in, ".."))
		return -1;
	strcpy(out, in);
	return 0;
}

/* ── USB sources: /mnt/usb0..7 are browsable and copyable-from,
 * never written (read-only sticks stay safe). */
static int
fs_usb_src(const char *in, char *out, size_t sz)
{
	if (!in || strlen(in) + 1 > sz)
		return -1;
	if (strncmp(in, "/mnt/usb", 8) != 0)
		return -1;
	if (in[8] < '0' || in[8] > '7')
		return -1;
	if (in[9] != '\0' && in[9] != '/')
		return -1;
	if (strstr(in, ".."))
		return -1;
	strcpy(out, in);
	return 0;
}

/* readable source: /data anywhere, or a USB stick */
static int
fs_src_jail(const char *in, char *out, size_t sz)
{
	if (fs_jail(in, out, sz) == 0)
		return 0;
	return fs_usb_src(in, out, sz);
}

/* join dir + name safely; 0 ok, -1 truncates */
static int
fs_join(char *dst, size_t sz, const char *dir, const char *name)
{
	int n = snprintf(dst, sz, "%s/%s", dir, name);

	return (n > 0 && (size_t)n < sz) ? 0 : -1;
}

/* recursive delete (files + dirs). best-effort: 0 ok, -1 on first error */
static int
fs_rm_r(const char *path)
{
	struct stat st;
	DIR *d;
	struct dirent *e;

	if (lstat(path, &st) != 0)
		return -1;
	if (!S_ISDIR(st.st_mode))
		return unlink(path);
	d = opendir(path);
	if (!d)
		return -1;
	while ((e = readdir(d)) != NULL) {
		char full[PATH_MAX_V];

		if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
			continue;
		if (fs_join(full, sizeof(full), path, e->d_name) != 0) {
			closedir(d);
			return -1;
		}
		if (fs_rm_r(full) != 0) {
			closedir(d);
			return -1;
		}
	}
	closedir(d);
	return rmdir(path);
}

/* copy one regular file, overwriting dst */
static int
fs_copy_file(const char *src, const char *dst)
{
	char *hb = malloc(256 * 1024);
	int in = -1, out = -1, rc = -1;
	ssize_t n;

	if (!hb)
		return -1;
	in = open(src, O_RDONLY);
	if (in < 0)
		goto out;
	out = open(dst, O_WRONLY | O_CREAT | O_TRUNC, 0644);
	if (out < 0)
		goto out;
	rc = 0;
	for (;;) {
		n = read(in, hb, 256 * 1024);
		if (n < 0) {
			if (errno == EINTR)
				continue;
			rc = -1;
			break;
		}
		if (n == 0)
			break;
		if (write(out, hb, (size_t)n) != n) {
			rc = -1;
			break;
		}
	}
out:
	free(hb);
	if (in >= 0)
		close(in);
	if (out >= 0)
		close(out);
	return rc;
}

/* recursive copy src -> dst (dst may exist when merging dirs) */
static int
fs_copy_r(const char *src, const char *dst)
{
	struct stat st;
	DIR *d;
	struct dirent *e;

	if (stat(src, &st) != 0)
		return -1;
	if (!S_ISDIR(st.st_mode))
		return fs_copy_file(src, dst);
	if (mkdir(dst, 0755) != 0 && errno != EEXIST)
		return -1;
	d = opendir(src);
	if (!d)
		return -1;
	while ((e = readdir(d)) != NULL) {
		char fsrc[PATH_MAX_V], fdst[PATH_MAX_V];

		if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
			continue;
		if (fs_join(fsrc, sizeof(fsrc), src, e->d_name) != 0 ||
		    fs_join(fdst, sizeof(fdst), dst, e->d_name) != 0) {
			closedir(d);
			return -1;
		}
		if (fs_copy_r(fsrc, fdst) != 0) {
			closedir(d);
			return -1;
		}
	}
	closedir(d);
	return 0;
}

/* escape " \ and C0 controls for JSON */
static void
json_escape(const char *src, char *dst, size_t dst_sz)
{
	size_t o = 0;

	while (*src && o + 1 < dst_sz) {
		unsigned char c = (unsigned char)*src++;
		if (c == '"' || c == '\\') {
			if (o + 2 >= dst_sz)
				break;
			dst[o++] = '\\';
			dst[o++] = (char)c;
		} else if (c < 0x20) {
			dst[o++] = ' ';
		} else {
			dst[o++] = (char)c;
		}
	}
	dst[o] = '\0';
}

typedef struct fs_entry {
	char name[256];
	int is_dir;
	long long size;
} fs_entry_t;

static int
fs_entry_cmp(const void *a, const void *b)
{
	const fs_entry_t *x = a, *y = b;

	if (x->is_dir != y->is_dir)
		return y->is_dir - x->is_dir; /* dirs first */
	return strcmp(x->name, y->name);
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
"<style>body{background:#171717;color:#F1F3F8;font-family:'Segoe UI',sans-serif;margin:0;padding:16px}"
"h2{color:#F1F3F8;margin:0 0 12px;font-size:20px}"
"#tabs{display:flex;gap:8px;margin-bottom:14px}"
"#tabs button{flex:1;padding:12px;background:#2A2A2A;border:none;border-radius:4px;color:#F1F3F8;font-size:15px;font-weight:bold;cursor:pointer}"
"#tabs button.on{background:#4F8EF7;color:#171717}"
"#pcrow{display:flex;gap:8px;margin-bottom:12px;align-items:center}"
"#pcstat{font-size:12px;color:#8B93A5;white-space:nowrap}"
"#tools{display:flex;gap:8px;margin-bottom:12px}"
"#tools input{flex:1}"
"#chips,#kind{display:flex;gap:6px}"
"#chips button,#kind button{padding:12px 14px;background:#2A2A2A;border:none;border-radius:4px;color:#F1F3F8;font-size:14px;cursor:pointer}"
"#chips button.on,#kind button.on{background:#4F8EF7;color:#171717}"
"#kind{margin-bottom:12px}"
"input{flex:1;padding:12px;border:1px solid #2A2A2A;border-radius:4px;background:#2A2A2A;color:#F1F3F8;font-size:15px}"
"button.go{padding:12px 16px;background:#4F8EF7;border:none;border-radius:4px;color:#171717;font-size:14px;font-weight:bold;cursor:pointer}"
"button.gh{padding:10px 14px;background:#404040;border:none;border-radius:4px;color:#F1F3F8;font-size:14px;cursor:pointer}"
"button.danger{padding:10px 14px;background:#E17B7B;border:none;border-radius:4px;color:#171717;font-size:14px;font-weight:bold;cursor:pointer}"
"#grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}"
".card{background:#202020;border-radius:8px;padding:10px;text-align:center}"
".card img{width:100%;height:120px;object-fit:contain;background:#171717}"
".card .t{font-size:13px;margin:8px 0 2px;min-height:32px}"
".card .m{font-size:11px;color:#8B93A5;margin-bottom:8px}"
".card button{width:100%;padding:10px;background:#4F8EF7;border:none;border-radius:4px;color:#171717;font-weight:bold;cursor:pointer}"
".big{font-size:44px}"
".fc{display:inline-block;font-size:11px;color:#4F8EF7;border:1px solid #4F8EF7;border-radius:10px;padding:2px 8px;margin-top:4px}"
".fambox{margin-top:8px;display:flex;flex-direction:column;gap:6px}"
".member{display:flex;gap:8px;align-items:center;background:#171717;border-radius:8px;padding:8px;text-align:left}"
".member .t{font-size:12px}.member .m{font-size:11px;color:#8B93A5}"
".member div:first-child{flex:1}"
".rb{font-size:10px;color:#171717;background:#4F8EF7;border-radius:4px;padding:2px 6px;margin-right:6px}"
".member button{padding:8px 12px;background:#4F8EF7;border:none;border-radius:4px;color:#171717;font-weight:bold;cursor:pointer}"
"#msg,#fmsg{margin-top:14px;font-size:15px;color:#8B93A5;min-height:24px}"
"#crumb,#mkrow,#usbrow{display:flex;gap:10px;margin-bottom:14px;align-items:center;flex-wrap:wrap}"
"#fpath{font-size:15px;color:#8B93A5}"
".frow{display:flex;gap:10px;align-items:center;background:#202020;border-radius:8px;padding:14px;margin-bottom:10px}"
".frow div:first-child{flex:1;font-size:17px}"
".frow .m{font-size:13px;color:#8B93A5}"
".frow div:last-child{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}"
".frow button{font-size:15px;padding:10px 14px}</style></head><body>"

"<h2>pkg remote installer</h2>"
"<div id=ver style='font-size:12px;color:#8B93A5;margin-bottom:12px'>page …</div>"
"<div id=tabs><button id=tabL class=on>Library</button><button id=tabF>Files</button></div>"
"<div id=lib>"
"<div id=pcrow><span id=pcstat>PC: ...</span>"
"<input id=pc placeholder='PC address'><button class=go id=save>Save</button></div>"
"<div id=tools><input id=q placeholder='Search title or ID...'>"
"<div id=chips><button data-p=all class=on>All</button><button data-p=PS5>PS5</button><button data-p=PS4>PS4</button></div></div>"
"<div id=kind><button data-k=games class=on>Games</button><button data-k=images>Images</button></div>"
"<div id=grid></div><div id=msg></div></div>"
"<div id=files style='display:none'>"
"<div id=usbrow><button class=gh data-u='/data'>Data</button>"
"<button class=gh data-u='/mnt/usb0'>USB0</button>"
"<button class=gh data-u='/mnt/usb1'>USB1</button>"
"<button class=gh data-u='/mnt/usb2'>USB2</button>"
"<button class=gh data-u='/mnt/usb3'>USB3</button></div>"
"<div id=crumb><button class=gh id=up>Up</button><span id=fpath>/data</span></div>"
"<div id=mkrow><input id=mkname placeholder='New folder name'><button class=go id=mkbtn>New folder</button></div>"
"<div id=flist></div><div id=fmsg></div></div>"
"<script>(function(){var PAGE_BUILD='" RECEIVER_BUILD "';"
"var verd=document.getElementById('ver');"
"verd.textContent='page '+PAGE_BUILD+' • receiver …';"
"fetch('/api/version').then(function(r){return r.text();}).then(function(t){"
"if(t.charAt(0)!=='{'){verd.textContent='page '+PAGE_BUILD+' • receiver: unknown (old payload? resend ELF)';return;}"
"var j=JSON.parse(t);"
"verd.textContent='page '+PAGE_BUILD+' • receiver '+j.build+((j.build===PAGE_BUILD)?'':' • MISMATCH — resend the newest ELF');"
"}).catch(function(){verd.textContent='page '+PAGE_BUILD+' • receiver: unreachable';});"
"var pcEl=document.getElementById('pc');"
"var pcstat=document.getElementById('pcstat');"
"var grid=document.getElementById('grid');var msg=document.getElementById('msg');"
"var qEl=document.getElementById('q');"
"var all=[],openFam=null,plat='all',kind='games';"
"var fpath='/data';"
"pcEl.value=localStorage.getItem('pri_pc')||'';"
"function show(t){document.getElementById('lib').style.display=t?'':'none';"
"document.getElementById('files').style.display=t?'none':'';"
"document.getElementById('tabL').className=t?'on':'';"
"document.getElementById('tabF').className=t?'':'on';"
"if(!t)fsLoad();}"
"document.getElementById('tabL').onclick=function(){show(1);};"
"document.getElementById('tabF').onclick=function(){show(0);};"
"function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;');}"
"function fmtSize(n){if(n<1024)return n+' B';if(n<1048576)return (n/1024).toFixed(1)+' KB';"
"if(n<1073741824)return (n/1048576).toFixed(1)+' MB';return (n/1073741824).toFixed(2)+' GB';}"
"function fmtSpd(b){if(b<1024)return b.toFixed(0)+' B/s';if(b<1048576)return (b/1024).toFixed(0)+' KB/s';"
"return (b/1048576).toFixed(1)+' MB/s';}"
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
"async function copyImg(id,file,size){msg.textContent='Copying '+file+'...';"
"var mode='overwrite';"
"try{var st=await fetch('/api/files/stat?path='+encodeURIComponent('/data/homebrew/'+file));"
"var stx=await st.text();"
"var sj=(stx.indexOf('error:')===0)?{exists:false,size:0}:JSON.parse(stx);"
"if(sj.exists&&size>0&&sj.size===size)"
"{if(!confirm(file+' is already there. OK = Overwrite, Cancel = stop.'))return;mode='overwrite';}"
"else if(sj.exists&&sj.size>0&&sj.size<size)"
"mode=confirm('Partial copy on console ('+fmtSize(sj.size)+' of '+fmtSize(size)+'). OK = Resume, Cancel = Overwrite from zero.')?'resume':'overwrite';}"
"catch(ex){}"
"try{var r=await fetch('/api/files/pull',{method:'POST',headers:{'Content-Type':'application/json'},"
"body:JSON.stringify({url:'http://'+pcEl.value+':9898/pkg/'+id,path:'/data/homebrew/'+file,mode:mode})});"
"var x=await r.text();"
"if(x.indexOf('started')<0){msg.textContent=x;return;}"
"var lastGot=0,lastT=Date.now();"
"for(var i=0;i<1800;i++){await new Promise(function(rs){setTimeout(rs,2000);});"
"try{var s=await fetch('/api/status');var j=await s.json();"
"if(!j.pull){msg.textContent='Copy started — watch the console notifications.';return;}"
"var now=Date.now(),spd='';"
"if(now>lastT&&j.pullGot>=lastGot)spd=' • '+fmtSpd((j.pullGot-lastGot)*1000/(now-lastT));"
"lastGot=j.pullGot;lastT=now;"
"if(j.pullWant>0){var pc=Math.floor(j.pullGot*100/j.pullWant);"
"msg.textContent='Copying '+file+': '+pc+'% ('+fmtSize(j.pullGot)+' / '+fmtSize(j.pullWant)+')'+spd;}"
"else msg.textContent='Copying '+file+': '+fmtSize(j.pullGot)+spd;}"
"catch(ex){msg.textContent='Copy started — watch the console notifications.';return;}}"
"msg.textContent='Copy started — watch the console notifications.';}"
"catch(ex){msg.textContent='Error: '+ex;}}"
"function imgIcon(f){if(f==='exfat')return '💽';if(f==='ffpkg'||f==='ffpfsc')return '🗜';return '📦';}"
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
"if(kind==='images'){"
"d.innerHTML='<div class=big>'+imgIcon(g.format)+'</div><div class=t>'+esc(g.title)+'</div><div class=m>'+esc(g.format)+' &middot; '+esc(g.sizeText||'')+'</div>';"
"var cb=document.createElement('button');cb.textContent='Copy to homebrew';"
"cb.onclick=function(ev){ev.stopPropagation();copyImg(g.id,g.file||g.title,g.size||0);};d.appendChild(cb);return d;}"
"var fam=all.filter(function(m){return m.format==='pkg'&&!isBase(m)&&m.familyKey===g.familyKey;});"
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
"function render(){grid.innerHTML='';var list;"
"if(kind==='images'){list=all.filter(function(g){return g.format!=='pkg'&&matchP(g)&&matchQ(g);});"
"list.sort(function(a,b){return a.title.toLowerCase()<b.title.toLowerCase()?-1:1;});"
"if(!list.length){msg.textContent=all.length?'No match.':'Library is empty — tick Publish library in PKG Sender.';return;}"
"msg.textContent=list.length+' images';"
"list.forEach(function(g){grid.appendChild(card(g,0));});return;}"
"var bases=all.filter(function(g){return g.format==='pkg'&&isBase(g)&&matchP(g)&&matchQ(g);});"
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
"function frow(e){var d=document.createElement('div');d.className='frow';"
"var ic=e.dir?'📁':'📄';"
"var sub=e.dir?'':(' &middot; '+fmtSize(e.size));"
"d.innerHTML='<div><span>'+ic+'</span> '+esc(e.name)+'<div class=m>'+(e.dir?'folder':('file'+sub))+'</div></div>';"
"var w=document.createElement('div');"
"function mkb(t,cls,fn){var b=document.createElement('button');b.className=cls;b.textContent=t;b.onclick=fn;w.appendChild(b);}"
"if(e.dir)mkb('Open','gh',function(){fpath=fpath+'/'+e.name;fsLoad();});"
"mkb('Info','gh',function(){fsInfo(e.name);});"
"mkb('Copy','gh',function(){fsCopy(e.name,e.dir);});"
"mkb('Move','gh',function(){fsMove(e.name);});"
"mkb('Rename','gh',function(){fsRename(e.name);});"
"mkb('Delete','danger',function(){if(confirm('Delete '+e.name+(e.dir?' (with everything inside)?':'?')))fsDel(e.name);});"
"d.appendChild(w);return d;}"
"async function fsApi(ep,obj){var fm=document.getElementById('fmsg');"
"try{var r=await fetch(ep,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(obj)});"
"var x=await r.text();fm.textContent=x;if(x.indexOf('ok')>=0)fsLoad();}"
"catch(ex){fm.textContent='Error: '+ex;}}"
"async function fsRename(name){var nn=prompt('Rename to:',name);"
"if(!nn||nn===name)return;"
"fsApi('/api/fs/rename',{from:fpath+'/'+name,to:fpath+'/'+nn});}"
"async function fsCopy(name,isDir){var dst=prompt('Copy to folder:',fpath);"
"if(!dst)return;"
"fsApi('/api/fs/copy',{src:fpath+'/'+name,dst:dst+'/'+name});}"
"async function fsMove(name){var dst=prompt('Move to folder:',fpath);"
"if(!dst||dst===fpath)return;"
"fsApi('/api/fs/move',{src:fpath+'/'+name,dst:dst+'/'+name});}"
"async function fsInfo(name){var fm=document.getElementById('fmsg');"
"try{var r=await fetch('/api/fs/info?path='+encodeURIComponent(fpath+'/'+name));"
"var txt=await r.text();"
"if(txt.charAt(0)!=='{'){fm.textContent='Reply: '+txt.substring(0,120);return;}"
"var j=JSON.parse(txt);"
"fm.textContent=j.name+' • '+j.kind+' • '+fmtSize(j.size)+' • '+j.magic;}"
"catch(ex){fm.textContent='Error: '+ex;}}"
"async function fsLoad(){var fp=document.getElementById('fpath');fp.textContent=fpath;"
"var fl=document.getElementById('flist');var fm=document.getElementById('fmsg');"
"fm.textContent='Loading...';fl.innerHTML='';"
"try{var r=await fetch('/api/fs/list?path='+encodeURIComponent(fpath));"
"var txt=await r.text();"
"if(txt.charAt(0)!=='{'){fm.textContent='Reply: '+txt.substring(0,120);return;}"
"var j=JSON.parse(txt);fm.textContent=j.entries.length+' entries'+(j.truncated?' (truncated)':'');"
"j.entries.forEach(function(e){fl.appendChild(frow(e));});}"
"catch(ex){fm.textContent='Error: '+ex;}}"
"async function fsDel(name){var fm=document.getElementById('fmsg');"
"try{var r=await fetch('/api/fs/delete',{method:'POST',headers:{'Content-Type':'application/json'},"
"body:JSON.stringify({path:fpath+'/'+name})});"
"var x=await r.text();fm.textContent=x;if(x.indexOf('ok')>=0)fsLoad();}"
"catch(ex){fm.textContent='Error: '+ex;}}"
"async function fsMkdir(){var inp=document.getElementById('mkname');var nm=inp.value.trim();"
"if(!nm)return;var fm=document.getElementById('fmsg');"
"try{var r=await fetch('/api/files/mkdir',{method:'POST',headers:{'Content-Type':'application/json'},"
"body:JSON.stringify({path:fpath+'/'+nm})});"
"var x=await r.text();fm.textContent=x;inp.value='';fsLoad();}"
"catch(ex){fm.textContent='Error: '+ex;}}"
"document.getElementById('save').onclick=load;"
"document.getElementById('mkbtn').onclick=fsMkdir;"
"document.getElementById('up').onclick=function(){var roots=['/data','/mnt/usb0','/mnt/usb1','/mnt/usb2','/mnt/usb3'];"
"if(roots.indexOf(fpath)>=0)return;var i=fpath.lastIndexOf('/');fpath=i>0?fpath.substring(0,i):'/data';fsLoad();};"
"var ub=document.getElementById('usbrow').children;"
"for(var u=0;u<ub.length;u++)(function(c){c.onclick=function(){fpath=c.getAttribute('data-u');fsLoad();};})(ub[u]);"
"qEl.oninput=render;"
"var chips=document.getElementById('chips').children;"
"for(var i=0;i<chips.length;i++)(function(c){c.onclick=function(){plat=c.getAttribute('data-p');"
"for(var k=0;k<chips.length;k++)chips[k].className='';c.className='on';render();};})(chips[i]);"
"var kinds=document.getElementById('kind').children;"
"for(var j=0;j<kinds.length;j++)(function(c){c.onclick=function(){kind=c.getAttribute('data-k');"
"for(var k=0;k<kinds.length;k++)kinds[k].className='';c.className='on';render();};})(kinds[j]);"
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

/* ── Pull downloader (Images tab: copy PC file -> /data/homebrew) ─────
 * Plain sequential HTTP GET (http only, the PC serves plain http).
 * Runs on a detached worker; skips when the same size is already there. */
typedef struct pull_job {
	char url[URL_MAX];
	char local[PATH_MAX_V];
	int resume;
} pull_job_t;

/* pull progress, visible in GET /api/status while a copy runs */
static volatile int g_pull_active = 0;
static volatile int g_pull_paused = 0;
static volatile long long g_pull_got = 0;
static volatile long long g_pull_want = -1;
static char g_pull_name[128];

/* 0 = ok, 1 = skipped (same size present), -1 = error */
#define PULL_SEGS 16
#define PULL_CHUNK (1024 * 1024)

typedef struct pull_seg {
	char host[256];
	char portstr[16];
	char get[URL_MAX];
	char local[PATH_MAX_V];
	long long start;
	long long len;
	int ok;
} pull_seg_t;

/* tuned socket (timeouts + big receive buffer), connected or -1 */
static int
pull_connect(const char *host, const char *portstr)
{
	struct addrinfo hints, *res = NULL, *rp;
	int s = -1;

	memset(&hints, 0, sizeof(hints));
	hints.ai_family = AF_INET;
	hints.ai_socktype = SOCK_STREAM;
	if (getaddrinfo(host, portstr, &hints, &res) != 0 || !res)
		return -1;
	for (rp = res; rp; rp = rp->ai_next) {
		s = socket(rp->ai_family, rp->ai_socktype, rp->ai_protocol);
		if (s < 0)
			continue;
		if (connect(s, rp->ai_addr, rp->ai_addrlen) == 0)
			break;
		close(s);
		s = -1;
	}
	freeaddrinfo(res);
	if (s >= 0) {
		struct timeval tv;
		int rcv = 4 * 1024 * 1024;

		tv.tv_sec = 30;
		tv.tv_usec = 0;
		setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
		setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
		setsockopt(s, SOL_SOCKET, SO_RCVBUF, &rcv, sizeof(rcv));
	}
	return s;
}

/* read a response header block; 1 = 2xx headers in hs, 0 = fail */
static int
pull_headers(int s, char *hs, size_t hs_sz)
{
	char c;
	size_t hl = 0;
	ssize_t n = -1;

	for (;;) {
		n = recv(s, &c, 1, 0);
		if (n <= 0)
			break;
		if (hl + 1 >= hs_sz)
			break;
		hs[hl++] = c;
		hs[hl] = '\0';
		if (hl >= 4 && !strcmp(hs + hl - 4, "\r\n\r\n"))
			break;
	}
	if (n <= 0 || hl < 12 || strncmp(hs, "HTTP/", 5) != 0)
		return 0;
	return hs[9] == '2';
}

/* Content-Length value in a header block, or -1 */
static long long
pull_clen(const char *hs)
{
	const char *p = strstr(hs, "Content-Length:");

	if (!p)
		p = strstr(hs, "content-length:");
	if (!p)
		return -1;
	return strtoll(p + 15, NULL, 10);
}

static ssize_t
pull_send(int s, const char *b, size_t n)
{
	size_t off = 0;

	while (off < n) {
		ssize_t w = send(s, b + off, n - off, 0);

		if (w < 0) {
			if (errno == EINTR)
				continue;
			return -1;
		}
		off += (size_t)w;
	}
	return (ssize_t)off;
}

/* one range segment: own connection, own fd, writes [start, start+len) */
static void *
pull_seg_worker(void *arg)
{
	pull_seg_t *sg = arg;
	char req[URL_MAX + 256], hs[4096];
	char *hb;
	int s = -1, out = -1;
	long long left;
	ssize_t n;

	sg->ok = 0;
	s = pull_connect(sg->host, sg->portstr);
	if (s < 0)
		return NULL;
	snprintf(req, sizeof(req),
	    "GET %s HTTP/1.0\r\nHost: %s\r\n"
	    "Range: bytes=%lld-%lld\r\nConnection: close\r\n\r\n",
	    sg->get, sg->host, sg->start, sg->start + sg->len - 1);
	if (pull_send(s, req, strlen(req)) < 0) {
		close(s);
		return NULL;
	}
	if (!pull_headers(s, hs, sizeof(hs))) {
		close(s);
		return NULL;
	}
	hb = malloc(PULL_CHUNK);
	out = open(sg->local, O_WRONLY);
	if (!hb || out < 0) {
		free(hb);
		if (out >= 0)
			close(out);
		close(s);
		return NULL;
	}
	if (lseek(out, (off_t)sg->start, SEEK_SET) == (off_t)-1) {
		free(hb);
		close(out);
		close(s);
		return NULL;
	}
	left = sg->len;
	while (left > 0) {
		size_t want = (size_t)(left < PULL_CHUNK ? left : PULL_CHUNK);
		size_t got = 0;

		while (g_pull_paused)
			sleep(1);
		n = recv(s, hb, want, 0);
		if (n < 0) {
			if (errno == EINTR)
				continue;
			break;
		}
		if (n == 0)
			break;
		while (got < (size_t)n) {
			ssize_t w = write(out, hb + got, (size_t)n - got);

			if (w < 0) {
				if (errno == EINTR)
					continue;
				break;
			}
			got += (size_t)w;
		}
		if (got != (size_t)n)
			break;
		left -= n;
		__sync_fetch_and_add(&g_pull_got, n);
	}
	free(hb);
	close(out);
	close(s);
	sg->ok = (left == 0);
	return NULL;
}

/* 0 = ok, 1 = skipped (same size present), -1 = error
 * resume = continue a partial local file instead of starting over. */
static int
pull_download(const char *url, const char *local, int resume)
{
	const char *p = url + 7; /* skip http:// */
	const char *slash = strchr(p, '/');
	char host[256], get[URL_MAX], req[URL_MAX + 256];
	char portstr[16] = "80";
	int s = -1, out = -1;
	ssize_t n;
	long long want = -1, got = 0, have = -1, base = 0;
	struct stat st;

	if (!slash || (size_t)(slash - p) >= sizeof(host))
		return -1;
	memcpy(host, p, (size_t)(slash - p));
	host[slash - p] = '\0';
	snprintf(get, sizeof(get), "%s", slash);
	p = strchr(host, ':');
	if (p) {
		snprintf(portstr, sizeof(portstr), "%s", p + 1);
		host[p - host] = '\0';
	}
	/* size discovery first (HEAD): big files go multi-segment below */
	{
		char hs[4096];

		s = pull_connect(host, portstr);
		if (s < 0)
			return -1;
		snprintf(req, sizeof(req),
		    "HEAD %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n",
		    get, host);
		if (pull_send(s, req, strlen(req)) < 0 ||
		    !pull_headers(s, hs, sizeof(hs)))
			want = -1;
		else
			want = pull_clen(hs);
		close(s);
		s = -1;
	}
	if (want >= 0 && stat(local, &st) == 0 &&
	    (long long)st.st_size == want)
		return 1; /* already there */
	/* resume point: existing partial bytes are kept, segments cover the rest */
	have = (want >= 0 && stat(local, &st) == 0) ? (long long)st.st_size : -1;
	base = (resume && want > 0 && have > 0 && have < want) ? have : 0;
	/* /data/homebrew may not exist yet — create the parent chain first */
	{
		char dir[PATH_MAX_V], *slash;

		snprintf(dir, sizeof(dir), "%s", local);
		slash = strrchr(dir, '/');
		if (slash && slash != dir) {
			*slash = '\0';
			if (mkdir_p(dir) != 0)
				return -1;
		}
	}
	g_pull_want = want;
	g_pull_got = base;
	if (want >= 2 * 1024 * 1024) {
		/* ── fast path: PULL_SEGS parallel Range streams ──
		 * heap, not stack: worker threads have small stacks. */
		pull_seg_t *segs = malloc(sizeof(*segs) * PULL_SEGS);
		pthread_t *tids = malloc(sizeof(*tids) * PULL_SEGS);
		long long part = (want - base) / PULL_SEGS;
		int i, alive = 0, fail = 0, rc = -1;

		if (!segs || !tids) {
			free(segs);
			free(tids);
			return -1;
		}

		out = open(local, O_WRONLY | O_CREAT | (base ? 0 : O_TRUNC), 0644);
		if (out < 0) {
			free(segs);
			free(tids);
			return -1;
		}
		/* pre-size so every segment has its range on disk
		 * (keeps resumed bytes: extend-only, never shrinks data) */
		if (ftruncate(out, (off_t)want) != 0) {
			close(out);
			free(segs);
			free(tids);
			return -1;
		}
		close(out);
		for (i = 0; i < PULL_SEGS; i++) {
			long long s0 = base + part * i;
			long long s1 = (i == PULL_SEGS - 1) ? want : base + part * (i + 1);

			snprintf(segs[i].host, sizeof(segs[i].host), "%s", host);
			snprintf(segs[i].portstr, sizeof(segs[i].portstr), "%s", portstr);
			snprintf(segs[i].get, sizeof(segs[i].get), "%s", get);
			snprintf(segs[i].local, sizeof(segs[i].local), "%s", local);
			segs[i].start = s0;
			segs[i].len = s1 - s0;
			if (segs[i].len <= 0) {
				segs[i].ok = 1; /* nothing left in this slice */
				continue;
			}
			segs[i].ok = 0;
			if (pthread_create(&tids[alive], NULL, pull_seg_worker, &segs[i]) != 0) {
				fail = 1;
				break;
			}
			alive++;
		}
		if (fail) {
			for (i = 0; i < alive; i++)
				pthread_join(tids[i], NULL);
			free(segs);
			free(tids);
			return -1;
		}
		for (i = 0; i < alive; i++)
			pthread_join(tids[i], NULL);
		rc = 0;
		for (i = 0; i < PULL_SEGS; i++)
			if (!segs[i].ok)
				rc = -1;
		free(segs);
		free(tids);
		if (rc != 0)
			return -1;
		if (stat(local, &st) != 0 || (long long)st.st_size != want)
			return -1;
		return 0;
	}
	/* ── small/unknown size: classic single stream ── */
	s = pull_connect(host, portstr);
	if (s < 0)
		return -1;
	if (base > 0)
		snprintf(req, sizeof(req),
		    "GET %s HTTP/1.0\r\nHost: %s\r\n"
		    "Range: bytes=%lld-\r\nConnection: close\r\n\r\n",
		    get, host, base);
	else
		snprintf(req, sizeof(req),
		    "GET %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n",
		    get, host);
	if (pull_send(s, req, strlen(req)) < 0) {
		close(s);
		return -1;
	}
	{
		char hs[4096];

		if (!pull_headers(s, hs, sizeof(hs))) {
			close(s);
			return -1;
		}
		if (want < 0)
			want = pull_clen(hs);
	}
	out = open(local, O_WRONLY | O_CREAT | (base ? 0 : O_TRUNC), 0644);
	if (out < 0) {
		close(s);
		return -1;
	}
	if (base > 0 && lseek(out, (off_t)base, SEEK_SET) == (off_t)-1) {
		close(s);
		close(out);
		return -1;
	}
	g_pull_want = want;
	g_pull_got = base;
	got = base;
	/* heap, not stack: PULL_CHUNK would risk the worker thread's stack */
	{
		char *hb = malloc(PULL_CHUNK);
		int done = 0;

		if (!hb) {
			close(s);
			close(out);
			return -1;
		}
		for (;;) {
			while (g_pull_paused)
				sleep(1);
			n = recv(s, hb, PULL_CHUNK, 0);
			if (n < 0) {
				if (errno == EINTR)
					continue;
				break;
			}
			if (n == 0) {
				done = 1;
				break;
			}
			if (write(out, hb, (size_t)n) != n)
				break;
			got += n;
			__sync_fetch_and_add(&g_pull_got, n);
		}
		free(hb);
		close(s);
		close(out);
		if (!done)
			return -1;
		if (want >= 0 && got != want)
			return -1;
		return 0;
	}
}

static void *
pull_worker(void *arg)
{
	pull_job_t *job = arg;
	char toast[256], base[128];
	const char *b = strrchr(job->local, '/');
	int rc;

	snprintf(base, sizeof(base), "%s", b ? b + 1 : job->local);
	snprintf(g_pull_name, sizeof(g_pull_name), "%s", base);
	/* keep /api/status JSON valid: no quotes/backslashes in the name */
	{
		char *q;

		for (q = g_pull_name; *q; q++)
			if (*q == '"' || *q == '\\')
				*q = '_';
	}
	g_pull_active = 1;
	__sync_fetch_and_add(&g_active_installs, 1);
 	rc = pull_download(job->url, job->local, job->resume);
	__sync_fetch_and_sub(&g_active_installs, 1);
	g_pull_active = 0;
	if (rc == 0)
		snprintf(toast, sizeof(toast), "Loopayeh: %s %s",
		    job->resume ? "resumed" : "copied", base);
	else if (rc == 1)
		snprintf(toast, sizeof(toast),
		    "Loopayeh: %s already there", base);
	else
		snprintf(toast, sizeof(toast),
		    "Loopayeh: copy failed %s", base);
	notify_user(toast);
	free(job);
	return NULL;
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
	/* absolute-form URIs (proxied browsers send
	 * "GET http://host:port/path?query HTTP/1.1"): strip to origin-form. */
	{
		const char *abs = NULL;

		if (!strncmp(path, "http://", 7))
			abs = strchr(path + 7, '/');
		else if (!strncmp(path, "https://", 8))
			abs = strchr(path + 8, '/');
		if (abs && abs != path)
			memmove(path, abs, strlen(abs) + 1);
		else if (!strncmp(path, "http://", 7) ||
		    !strncmp(path, "https://", 8)) {
			strcpy(path, "/"); /* "http://host" with no path */
		} else if (path[0] == '\0' || path[0] != '/') {
			free(buf);
			close(fd);
			return;
		}
		snprintf(g_last_req, sizeof(g_last_req), "%s %s", method, path);
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
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/pull/pause", 15)) {
		long long paused = 1;

		json_long(body, "paused", &paused);
		g_pull_paused = paused ? 1 : 0;
		send_json(fd, g_pull_paused ? "{\"ok\":true,\"paused\":true}"
		    : "{\"ok\":true,\"paused\":false}");
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/version", 12)) {
		send_json(fd, "{\"build\":\"" RECEIVER_BUILD "\"}");
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/dbg", 8)) {
		char out[320], esc[256];

		json_escape(g_last_req, esc, sizeof(esc));
		snprintf(out, sizeof(out), "{\"last\":\"%s\"}", esc);
		send_json(fd, out);
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/status", 11)) {
		char out[256];

		snprintf(out, sizeof(out), "{\"busy\":%s,\"active\":%d,"
		    "\"pull\":%s,\"pullName\":\"%s\","
		    "\"pullGot\":%lld,\"pullWant\":%lld,\"pullPaused\":%s}",
		    g_active_installs > 0 ? "true" : "false",
		    g_active_installs,
		    g_pull_active ? "true" : "false",
		    g_pull_active ? g_pull_name : "",
		    g_pull_got, g_pull_want,
		    g_pull_paused ? "true" : "false");
		send_json(fd, out);
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/pc", 7)) {
		char out[128];
		time_t now = time(NULL);
		long age = g_pc_seen > 0 ? (long)(now - g_pc_seen) : -1;

		snprintf(out, sizeof(out), "{\"pc\":\"%s\",\"age\":%ld}",
		    g_pc_addr, age);
		send_json(fd, out);
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/fs/list", 13)) {
		char rpath[PATH_MAX_V], local[PATH_MAX_V];
		char escpath[PATH_MAX_V * 2];
		DIR *dp;
		struct dirent *de;
		fs_entry_t *ents;
		size_t n = 0;
		char *json;
		size_t jlen = 65536, joff;
		int truncated = 0;

		if (!query_param(path, "path", rpath, sizeof(rpath)) ||
		    fs_src_jail(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad path");
		} else if (!(dp = opendir(local))) {
			send_text(fd, "error:not a directory");
		} else {
			ents = malloc(sizeof(fs_entry_t) * FS_MAX_ENTRIES);
			if (ents) {
				while (n < FS_MAX_ENTRIES &&
				    (de = readdir(dp)) != NULL) {
					int is_dir;
					if (!strcmp(de->d_name, ".") ||
					    !strcmp(de->d_name, ".."))
						continue;
					if (de->d_type == DT_UNKNOWN) {
						char full[PATH_MAX_V * 2];
						struct stat st;
						snprintf(full, sizeof(full),
						    "%s/%s", local, de->d_name);
						is_dir = stat(full, &st) == 0 &&
						    S_ISDIR(st.st_mode);
					} else {
						is_dir = de->d_type == DT_DIR;
					}
					snprintf(ents[n].name,
					    sizeof(ents[n].name), "%s",
					    de->d_name);
					ents[n].is_dir = is_dir;
					ents[n].size = 0;
					if (!is_dir) {
						char full[PATH_MAX_V * 2];
						struct stat st;
						snprintf(full, sizeof(full),
						    "%s/%s", local, de->d_name);
						if (stat(full, &st) == 0)
							ents[n].size =
							    (long long)st.st_size;
					}
					n++;
				}
				if (n == FS_MAX_ENTRIES &&
				    readdir(dp) != NULL)
					truncated = 1;
				qsort(ents, n, sizeof(fs_entry_t),
				    fs_entry_cmp);
			}
			closedir(dp);
			json_escape(local, escpath, sizeof(escpath));
			json = malloc(jlen);
			if (!ents || !json) {
				free(ents);
				free(json);
				send_text(fd, "error:out of memory");
			} else {
				size_t i, toff;
				joff = (size_t)snprintf(json, jlen,
				    "{\"path\":\"%s\",\"truncated\":",
				    escpath);
				toff = joff; /* single flag digit patched below */
				joff += (size_t)snprintf(json + joff,
				    jlen - joff, "0,\"entries\":[");
				for (i = 0; i < n; i++) {
					char nm[512], row[800];
					int need;
					json_escape(ents[i].name, nm,
					    sizeof(nm));
					need = snprintf(row, sizeof(row),
					    "%s{\"name\":\"%s\",\"dir\":%s,\"size\":%lld}",
					    i ? "," : "", nm,
					    ents[i].is_dir ? "true" : "false",
					    ents[i].size);
					if (joff + (size_t)need + 32 >= jlen) {
						truncated = 1;
						break;
					}
					memcpy(json + joff, row,
					    (size_t)need);
					joff += (size_t)need;
				}
				free(ents);
				if (truncated)
					json[toff] = '1';
				memcpy(json + joff, "]}", 3);
				send_json(fd, json);
				free(json);
			}
		}
	} else if (!strcmp(method, "GET") &&
	           !strncmp(path, "/api/fs/info", 13)) {
		char rpath[PATH_MAX_V], local[PATH_MAX_V];
		char escname[512];
		struct stat st;
		const char *slash, *dot, *kind = "file";
		char magic[33] = "";
		char out[2048];
		int need;

		if (!query_param(path, "path", rpath, sizeof(rpath)) ||
		    fs_src_jail(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad path");
		} else if (stat(local, &st) != 0) {
			send_text(fd, "error:not found");
		} else {
			if (S_ISDIR(st.st_mode)) {
				kind = "folder";
			} else {
				int f = open(local, O_RDONLY);
				if (f >= 0) {
					unsigned char hb[16];
					ssize_t n = read(f, hb, sizeof(hb));
					int i;
					for (i = 0; i < n; i++)
						snprintf(magic + i * 2, 3, "%02x", hb[i]);
					close(f);
				}
				slash = strrchr(local, '/');
				dot = strrchr(slash ? slash : local, '.');
				if (dot) {
					if (!strcasecmp(dot, ".pkg"))
						kind = "pkg";
					else if (!strcasecmp(dot, ".exfat") ||
					    !strcasecmp(dot, ".ffpkg") ||
					    !strcasecmp(dot, ".ffpfsc"))
						kind = "image";
					else if (!strcasecmp(dot, ".elf"))
						kind = "elf";
					else if (!strcasecmp(dot, ".png") ||
					    !strcasecmp(dot, ".jpg") ||
					    !strcasecmp(dot, ".jpeg"))
						kind = "picture";
					else if (!strcasecmp(dot, ".json"))
						kind = "json";
				}
			}
			slash = strrchr(local, '/');
			json_escape(slash ? slash + 1 : local, escname, sizeof(escname));
			need = snprintf(out, sizeof(out),
			    "{\"name\":\"%s\",\"kind\":\"%s\",\"size\":%lld,"
			    "\"mtime\":%lld,\"magic\":\"%s\"}",
			    escname, kind,
			    S_ISDIR(st.st_mode) ? 0 : (long long)st.st_size,
			    (long long)st.st_mtime, magic);
			if (need < 0 || (size_t)need >= sizeof(out))
				send_text(fd, "error:name too long");
			else
				send_json(fd, out);
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/fs/rename", 15)) {
		char rfrom[PATH_MAX_V], rto[PATH_MAX_V];
		char from[PATH_MAX_V], to[PATH_MAX_V];

		if (!json_string(body, "from", rfrom, sizeof(rfrom)) ||
		    !json_string(body, "to", rto, sizeof(rto)) ||
		    fs_jail(rfrom, from, sizeof(from)) != 0 ||
		    fs_jail(rto, to, sizeof(to)) != 0) {
			send_text(fd, "error:bad path");
		} else if (!strcmp(from, to)) {
			send_text(fd, "error:same path");
		} else if (!strcmp(to, FS_ROOT)) {
			send_text(fd, "error:bad path");
		} else if (rename(from, to) == 0) {
			send_json(fd, "{\"ok\":true}");
		} else {
			send_text(fd, "error:rename failed");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/fs/copy", 13)) {
		char rsrc[PATH_MAX_V], rdst[PATH_MAX_V];
		char src[PATH_MAX_V], dst[PATH_MAX_V];

		if (!json_string(body, "src", rsrc, sizeof(rsrc)) ||
		    !json_string(body, "dst", rdst, sizeof(rdst)) ||
		    fs_src_jail(rsrc, src, sizeof(src)) != 0 ||
		    fs_jail(rdst, dst, sizeof(dst)) != 0) {
			send_text(fd, "error:bad path");
		} else if (!strcmp(dst, FS_ROOT)) {
			send_text(fd, "error:bad path");
		} else if (fs_copy_r(src, dst) == 0) {
			send_json(fd, "{\"ok\":true}");
		} else {
			send_text(fd, "error:copy failed");
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/fs/move", 13)) {
		char rsrc[PATH_MAX_V], rdst[PATH_MAX_V];
		char src[PATH_MAX_V], dst[PATH_MAX_V];

		if (!json_string(body, "src", rsrc, sizeof(rsrc)) ||
		    !json_string(body, "dst", rdst, sizeof(rdst)) ||
		    fs_src_jail(rsrc, src, sizeof(src)) != 0 ||
		    fs_jail(rdst, dst, sizeof(dst)) != 0) {
			send_text(fd, "error:bad path");
		} else if (!strcmp(dst, FS_ROOT)) {
			send_text(fd, "error:bad path");
		} else if (rename(src, dst) == 0) {
			send_json(fd, "{\"ok\":true}");
		} else if (errno == EXDEV && fs_copy_r(src, dst) == 0 &&
		    fs_rm_r(src) == 0) {
			send_json(fd, "{\"ok\":true}");
		} else {
			send_text(fd, "error:move failed");
		}
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
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/fs/delete", 14)) {
		char rpath[PATH_MAX_V], local[PATH_MAX_V];
		struct stat st;

		if (!json_string(body, "path", rpath, sizeof(rpath)) ||
		    fs_jail(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad path");
		} else if (!strcmp(local, FS_ROOT)) {
			send_text(fd, "error:refusing to delete root");
		} else if (stat(local, &st) != 0) {
			send_text(fd, "error:not found");
		} else if (S_ISDIR(st.st_mode)) {
			if (fs_rm_r(local) == 0) {
				send_json(fd, "{\"ok\":true}");
			} else {
				send_text(fd, "error:delete failed");
			}
		} else {
			if (unlink(local) == 0) {
				send_json(fd, "{\"ok\":true}");
			} else {
				send_text(fd, "error:delete failed");
			}
		}
	} else if (!strcmp(method, "POST") &&
	           !strncmp(path, "/api/files/pull", 15)) {
		char url[URL_MAX], rpath[PATH_MAX_V], local[PATH_MAX_V];
		char mode[16] = "";

		if (!json_string(body, "url", url, sizeof(url)) ||
		    !json_string(body, "path", rpath, sizeof(rpath)) ||
		    strncmp(url, "http://", 7) != 0 ||
		    jail_path(rpath, local, sizeof(local)) != 0) {
			send_text(fd, "error:bad url/path");
		} else {
			pull_job_t *job = malloc(sizeof(*job));
			pthread_t tid;
			json_string(body, "mode", mode, sizeof(mode));
			if (!job) {
				send_text(fd, "error:out of memory");
			} else {
				snprintf(job->url, sizeof(job->url),
				    "%s", url);
				snprintf(job->local, sizeof(job->local),
				    "%s", local);
				job->resume = !strcmp(mode, "resume");
				if (pthread_create(&tid, NULL, pull_worker,
				    job) != 0) {
					free(job);
					send_text(fd, "error:worker failed");
				} else {
					pthread_detach(tid);
					send_json(fd,
					    "{\"ok\":true,\"started\":true}");
				}
			}
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
