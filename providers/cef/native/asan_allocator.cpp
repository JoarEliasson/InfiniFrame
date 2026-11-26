// Linux address-sanitizer builds only. CEF's explicit glibc allocator dispatch
// must share ASan's allocator with intercepted functions such as strdup.
#include <cstdlib>
#include <malloc.h>

// The sanitizer runtime exports this checked implementation, including invalid,
// interior and already-freed pointer diagnostics. No ownership fallback is used.
extern "C" size_t __interceptor_malloc_usable_size(void* pointer);

#define ASAN_ALLOCATOR_EXPORT __attribute__((visibility("default")))
extern "C" {
ASAN_ALLOCATOR_EXPORT void* __libc_malloc(size_t size) noexcept { return std::malloc(size); }
ASAN_ALLOCATOR_EXPORT void* __libc_calloc(size_t count, size_t size) noexcept { return std::calloc(count, size); }
ASAN_ALLOCATOR_EXPORT void* __libc_realloc(void* pointer, size_t size) noexcept { return std::realloc(pointer, size); }
ASAN_ALLOCATOR_EXPORT void __libc_free(void* pointer) noexcept { std::free(pointer); }
ASAN_ALLOCATOR_EXPORT size_t malloc_usable_size(void* pointer) noexcept {
  return pointer ? __interceptor_malloc_usable_size(pointer) : 0;
}
}
// Do not override __libc_memalign: ASan's existing interceptor also performs
// dynamic-TLS bookkeeping. CMake places this DSO after CEF and before libc so
// CEF's dlsym(RTLD_NEXT, "malloc_usable_size") finds this checked implementation.
