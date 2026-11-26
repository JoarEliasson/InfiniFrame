#include <cassert>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>

extern "C" void* InfiniFrameAsanNextUsableSize();
using Allocate = void* (*)(size_t);
using AllocateZero = void* (*)(size_t, size_t);
using Reallocate = void* (*)(void*, size_t);
using Release = void (*)(void*);
using Usable = size_t (*)(void*);
using Aligned = void* (*)(size_t, size_t);

template<class Function> Function Lookup(void* handle, const char* name) {
  auto pointer = dlsym(handle, name);
  assert(pointer);
  return reinterpret_cast<Function>(pointer);
}
const char* Provider(void* function) {
  Dl_info info{};
  assert(dladdr(function, &info));
  assert(info.dli_fname);
  return info.dli_fname;
}
int main(int argc, char** argv) {
  assert(argc == 2);
  void* adapter = dlopen("libInfiniFrame.Cef.AsanAllocator.so", RTLD_NOW | RTLD_NOLOAD);
  assert(adapter);
  auto allocate = Lookup<Allocate>(adapter, "__libc_malloc");
  auto zero = Lookup<AllocateZero>(adapter, "__libc_calloc");
  auto resize = Lookup<Reallocate>(adapter, "__libc_realloc");
  auto release = Lookup<Release>(adapter, "__libc_free");
  auto own_size = Lookup<Usable>(adapter, "malloc_usable_size");
  auto next = InfiniFrameAsanNextUsableSize();
  assert(next == reinterpret_cast<void*>(own_size));
  assert(std::strstr(Provider(next), "libInfiniFrame.Cef.AsanAllocator.so"));
  assert(dlsym(RTLD_DEFAULT, "__libc_malloc") == reinterpret_cast<void*>(allocate));
  assert(dlsym(RTLD_DEFAULT, "__libc_calloc") == reinterpret_cast<void*>(zero));
  assert(dlsym(RTLD_DEFAULT, "__libc_realloc") == reinterpret_cast<void*>(resize));
  assert(dlsym(RTLD_DEFAULT, "__libc_free") == reinterpret_cast<void*>(release));
  auto aligned = Lookup<Aligned>(RTLD_DEFAULT, "__libc_memalign");
  auto aligned_interceptor = dlsym(RTLD_DEFAULT, "__interceptor___libc_memalign");
  assert(aligned_interceptor);
  assert(reinterpret_cast<void*>(aligned) == aligned_interceptor);
  std::printf("PROVIDER RTLD_NEXT malloc_usable_size=%s; __libc_memalign=%s\n",
              Provider(next), Provider(reinterpret_cast<void*>(aligned)));
  std::fflush(stdout);
  auto size = reinterpret_cast<Usable>(next);
  const char* mode = argv[1];
  if (std::strcmp(mode, "positive") == 0) {
    assert(size(nullptr) == 0);
    release(nullptr);
    auto* p = static_cast<unsigned char*>(allocate(32));
    assert(p && size(p) >= 32);
    std::memset(p, 0x42, 32);
    p = static_cast<unsigned char*>(resize(p, 64));
    assert(p && size(p) >= 64);
    for (int i = 0; i < 32; ++i) assert(p[i] == 0x42);
    release(p);
    p = static_cast<unsigned char*>(zero(8, 4));
    assert(p && size(p) >= 32);
    for (int i = 0; i < 32; ++i) assert(p[i] == 0);
    release(p);
    p = static_cast<unsigned char*>(resize(nullptr, 17));
    assert(p && size(p) >= 17);
    release(p);
    p = static_cast<unsigned char*>(aligned(64, 65));
    assert(p && reinterpret_cast<uintptr_t>(p) % 64 == 0 && size(p) >= 65);
    release(p);
    assert(dlclose(adapter) == 0);
    std::puts("PASS allocator families, data preservation, zeroing, alignment, null and RTLD_NEXT provider");
    return 0;
  }
  auto* p = static_cast<unsigned char*>(allocate(16));
  assert(p);
  if (std::strcmp(mode, "oob") == 0) {
    static_cast<volatile unsigned char*>(p)[16] = 1;
  } else if (std::strcmp(mode, "oob-calloc") == 0) {
    release(p); p = static_cast<unsigned char*>(zero(4, 4));
    static_cast<volatile unsigned char*>(p)[16] = 1;
  } else if (std::strcmp(mode, "oob-realloc") == 0) {
    p = static_cast<unsigned char*>(resize(p, 32));
    static_cast<volatile unsigned char*>(p)[32] = 1;
  } else if (std::strcmp(mode, "uaf") == 0) {
    release(p); static_cast<volatile unsigned char*>(p)[0] = 1;
  } else if (std::strcmp(mode, "doublefree") == 0) {
    release(p); release(p);
  } else if (std::strcmp(mode, "size-invalid") == 0) {
    int local = 0; std::printf("unexpected size %zu\n", size(&local));
  } else if (std::strcmp(mode, "size-interior") == 0) {
    std::printf("unexpected size %zu\n", size(p + 1));
  } else if (std::strcmp(mode, "size-freed") == 0) {
    release(p); std::printf("unexpected size %zu\n", size(p));
  } else {
    std::fputs("unknown control\n", stderr); return 64;
  }
  std::fputs("FAIL intended ASan violation was not rejected\n", stderr);
  return 70;
}
