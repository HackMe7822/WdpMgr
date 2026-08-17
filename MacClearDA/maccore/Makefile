SDK     := $(shell xcrun --show-sdk-path 2>/dev/null)
DYLIB   := maccore.dylib
CFLAGS  := -dynamiclib -framework Cocoa \
            -arch arm64 -arch x86_64 \
            -O2 -Wall

all: $(DYLIB)

$(DYLIB): maccore.m
	clang $(CFLAGS) $(if $(SDK),-isysroot $(SDK)) -o $@ $<
	@# Strip signature so DYLD_INSERT_LIBRARIES can load it without ad-hoc issues
	codesign --remove-signature $@ 2>/dev/null || true
	@echo "Built: $(DYLIB)"

clean:
	rm -f $(DYLIB)
