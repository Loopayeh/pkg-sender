ifdef PS5_PAYLOAD_SDK
    include $(PS5_PAYLOAD_SDK)/toolchain/prospero.mk
else
    $(error PS5_PAYLOAD_SDK is undefined)
endif

ELF := pkg-receiver.elf

CFLAGS := -Os -Wall -Werror

ifdef TEST_ONLY
    CFLAGS += -DTEST_ONLY
    ELF := pkg-receiver-testonly.elf
endif

all: $(ELF)

$(ELF): main.c
	$(CC) $(CFLAGS) -o $@ $^ -lkernel_sys -lSceNet
	$(STRIP) --strip-all $@

clean:
	rm -f $(ELF)
