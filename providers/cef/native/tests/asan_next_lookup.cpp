#include <dlfcn.h>
// Stand-in for the existing closed CEF DSO's exact RTLD_NEXT lookup. This
// standalone control never loads or executes CEF.
extern "C" __attribute__((visibility("default")))
void* InfiniFrameAsanNextUsableSize() {
  return dlsym(RTLD_NEXT, "malloc_usable_size");
}
