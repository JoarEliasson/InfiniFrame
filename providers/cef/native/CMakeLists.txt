cmake_minimum_required(VERSION 3.24)
project(InfiniFrameCefNative LANGUAGES C CXX)
if(CMAKE_INSTALL_PREFIX_INITIALIZED_TO_DEFAULT)
  set(CMAKE_INSTALL_PREFIX "${CMAKE_BINARY_DIR}/installed" CACHE PATH "Dedicated companion payload directory" FORCE)
endif()

if(APPLE OR NOT (WIN32 OR CMAKE_SYSTEM_NAME STREQUAL "Linux"))
  message(FATAL_ERROR "Only Windows and Linux are supported.")
endif()
if(NOT IS_ABSOLUTE "${CEF_ROOT}" OR NOT EXISTS "${CEF_ROOT}/cmake/FindCEF.cmake")
  message(FATAL_ERROR "Supply -DCEF_ROOT=<existing official CEF binary distribution>.")
endif()
file(TO_CMAKE_PATH "${CEF_ROOT}" CEF_ROOT)
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Only x64 and ARM64 are supported.")
endif()
set(USE_SANDBOX ON CACHE BOOL "Sandbox is mandatory" FORCE)
if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64" OR CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
  set(PROJECT_ARCH arm64)
elseif(CMAKE_GENERATOR_PLATFORM STREQUAL "x64" OR CMAKE_SYSTEM_PROCESSOR MATCHES "^(AMD64|amd64|x86_64|x64)$")
  set(PROJECT_ARCH x86_64)
else()
  message(FATAL_ERROR "The target processor must be x64 or ARM64.")
endif()
set(api_version 15600 CACHE STRING "Stable CEF API")
if(NOT api_version STREQUAL "15600")
  message(FATAL_ERROR "This companion targets stable CEF API 15600.")
endif()
list(APPEND CMAKE_MODULE_PATH "${CEF_ROOT}/cmake")
find_package(CEF REQUIRED)
if(OS_LINUX)
  # CEF's sample flag searches the process working directory. Installed
  # applications must resolve companion libraries beside their executable.
  list(REMOVE_ITEM CEF_LINKER_FLAGS "-Wl,-rpath,.")
endif()
add_subdirectory("${CEF_LIBCEF_DLL_WRAPPER_PATH}" libcef_dll_wrapper)
ADD_LOGICAL_TARGET(libcef_lib "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")
if(WIN32)
  add_library(InfiniFrame.Cef.Host SHARED host.cpp renderer.cpp protocol.cpp)
  SET_LIBRARY_TARGET_PROPERTIES(InfiniFrame.Cef.Host)
else()
  add_executable(InfiniFrame.Cef.Host host.cpp renderer.cpp protocol.cpp)
  SET_EXECUTABLE_TARGET_PROPERTIES(InfiniFrame.Cef.Host)
  set_target_properties(InfiniFrame.Cef.Host PROPERTIES
    INSTALL_RPATH "$ORIGIN" BUILD_WITH_INSTALL_RPATH TRUE)
endif()
target_compile_features(InfiniFrame.Cef.Host PRIVATE cxx_std_20)
option(INFINIFRAME_CEF_ENABLE_SANITIZERS "Instrument companion-owned Linux code with ASan/UBSan" OFF)
set(INFINIFRAME_CEF_ADDRESS_SANITIZER FALSE)
if(INFINIFRAME_CEF_ENABLE_SANITIZERS)
  if(NOT OS_LINUX OR NOT CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    message(FATAL_ERROR "Companion sanitizers require a supported Linux GCC/Clang toolchain.")
  endif()
  set(INFINIFRAME_CEF_SANITIZERS "address,undefined" CACHE STRING "Companion sanitizer instrumentation")
  set_property(CACHE INFINIFRAME_CEF_SANITIZERS PROPERTY STRINGS "address,undefined" "address" "undefined")
  if(NOT INFINIFRAME_CEF_SANITIZERS MATCHES "^(address,undefined|address|undefined)$")
    message(FATAL_ERROR "Select address, undefined, or address,undefined sanitizers.")
  endif()
  target_compile_options(InfiniFrame.Cef.Host PRIVATE "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}" -fno-omit-frame-pointer)
  target_link_options(InfiniFrame.Cef.Host PRIVATE "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}")
  if(INFINIFRAME_CEF_SANITIZERS MATCHES "(^|,)address(,|$)")
    set(INFINIFRAME_CEF_ADDRESS_SANITIZER TRUE)
  endif()
endif()
set(HOST_PLATFORM_LIBS ${CEF_STANDARD_LIBS})
if(OS_LINUX)
  # Views needs no Xlib headers. Runtime-only desktops can have the versioned
  # SONAME without the development package's unversioned linker symlink.
  find_library(HOST_X11_LIBRARY NAMES X11 libX11.so.6 REQUIRED)
  list(REMOVE_ITEM HOST_PLATFORM_LIBS X11)
  list(APPEND HOST_PLATFORM_LIBS "${HOST_X11_LIBRARY}")
endif()
if(INFINIFRAME_CEF_ADDRESS_SANITIZER)
  # CEF's private glibc dispatch and its RTLD_NEXT size lookup must share the
  # checked ASan allocator. Preserve dependency order, even with --as-needed.
  add_library(InfiniFrame.Cef.AsanAllocator SHARED asan_allocator.cpp)
  target_compile_features(InfiniFrame.Cef.AsanAllocator PRIVATE cxx_std_20)
  target_compile_options(InfiniFrame.Cef.AsanAllocator PRIVATE
    "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}" -fno-omit-frame-pointer -fvisibility=hidden)
  target_link_options(InfiniFrame.Cef.AsanAllocator PRIVATE "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}")
  set_target_properties(InfiniFrame.Cef.AsanAllocator PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>"
    INSTALL_RPATH "$ORIGIN" BUILD_WITH_INSTALL_RPATH TRUE)
  target_compile_definitions(InfiniFrame.Cef.Host PRIVATE INFINIFRAME_CEF_ADDRESS_SANITIZER=1)
  target_link_libraries(InfiniFrame.Cef.Host PRIVATE libcef_lib libcef_dll_wrapper
    "-Wl,--push-state,--no-as-needed" InfiniFrame.Cef.AsanAllocator "-Wl,--pop-state" ${HOST_PLATFORM_LIBS})
else()
  target_link_libraries(InfiniFrame.Cef.Host PRIVATE libcef_lib libcef_dll_wrapper ${HOST_PLATFORM_LIBS})
endif()
if(WIN32)
  target_link_libraries(InfiniFrame.Cef.Host PRIVATE bcrypt)
  target_compile_options(InfiniFrame.Cef.Host PRIVATE /EHsc)
else()
  target_compile_options(InfiniFrame.Cef.Host PRIVATE -fexceptions)
endif()
if(PROJECT_ARCH STREQUAL "arm64")
  set(COMPANION_ARCH arm64)
else()
  set(COMPANION_ARCH x64)
endif()
if(WIN32)
  set(COMPANION_RID "win-${COMPANION_ARCH}")
else()
  set(COMPANION_RID "linux-${COMPANION_ARCH}")
endif()
target_compile_definitions(InfiniFrame.Cef.Host PRIVATE COMPANION_RID="${COMPANION_RID}")
set(HOST_LAYOUT "${CMAKE_BINARY_DIR}/runtime/$<CONFIG>")
set_target_properties(InfiniFrame.Cef.Host PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>")
if(INFINIFRAME_CEF_ADDRESS_SANITIZER)
  # The stock CEF child sandbox imposes finite data limits incompatible with
  # ASan's enormous shadow mappings. Keep the same-source child sandboxed and
  # uninstrumented; the separate UBSan-only build covers renderer execution.
  add_executable(InfiniFrame.Cef.Subprocess host.cpp renderer.cpp protocol.cpp)
  SET_EXECUTABLE_TARGET_PROPERTIES(InfiniFrame.Cef.Subprocess)
  target_compile_features(InfiniFrame.Cef.Subprocess PRIVATE cxx_std_20)
  target_compile_options(InfiniFrame.Cef.Subprocess PRIVATE -fexceptions)
  target_compile_definitions(InfiniFrame.Cef.Subprocess PRIVATE COMPANION_RID="${COMPANION_RID}")
  target_link_libraries(InfiniFrame.Cef.Subprocess PRIVATE libcef_lib libcef_dll_wrapper ${HOST_PLATFORM_LIBS})
  set_target_properties(InfiniFrame.Cef.Subprocess PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>"
    INSTALL_RPATH "$ORIGIN" BUILD_WITH_INSTALL_RPATH TRUE)
  add_dependencies(InfiniFrame.Cef.Host InfiniFrame.Cef.Subprocess)

  # Optional standalone controls never load CEF and are excluded from payloads.
  add_library(InfiniFrame.Cef.AsanLookup SHARED EXCLUDE_FROM_ALL tests/asan_next_lookup.cpp)
  add_executable(InfiniFrame.Cef.AsanControls EXCLUDE_FROM_ALL tests/asan_controls.cpp)
  foreach(target InfiniFrame.Cef.AsanLookup InfiniFrame.Cef.AsanControls)
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_options(${target} PRIVATE "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}" -fno-omit-frame-pointer -UNDEBUG)
    target_link_options(${target} PRIVATE "-fsanitize=${INFINIFRAME_CEF_SANITIZERS}")
    set_target_properties(${target} PROPERTIES
      RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/asan-controls/$<CONFIG>"
      LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/asan-controls/$<CONFIG>"
      INSTALL_RPATH "$ORIGIN" BUILD_WITH_INSTALL_RPATH TRUE)
  endforeach()
  set_target_properties(InfiniFrame.Cef.AsanLookup PROPERTIES OUTPUT_NAME InfiniFrame.AsanNextLookupProbe)
  # RTLD_NEXT identifies the caller's DSO from its return address. A Release
  # tail call would incorrectly make the control executable the lookup origin.
  target_compile_options(InfiniFrame.Cef.AsanLookup PRIVATE -fno-optimize-sibling-calls)
  target_link_libraries(InfiniFrame.Cef.AsanLookup PRIVATE ${CMAKE_DL_LIBS})
  target_link_libraries(InfiniFrame.Cef.AsanControls PRIVATE InfiniFrame.Cef.AsanLookup
    "-Wl,--push-state,--no-as-needed" InfiniFrame.Cef.AsanAllocator "-Wl,--pop-state" ${CMAKE_DL_LIBS})
  add_custom_command(TARGET InfiniFrame.Cef.AsanControls POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:InfiniFrame.Cef.AsanAllocator>"
      "$<TARGET_FILE_DIR:InfiniFrame.Cef.AsanControls>/$<TARGET_FILE_NAME:InfiniFrame.Cef.AsanAllocator>" VERBATIM)
endif()
add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
  COMMAND "${CMAKE_COMMAND}" -E rm -rf "${HOST_LAYOUT}"
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${HOST_LAYOUT}"
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:InfiniFrame.Cef.Host>" "${HOST_LAYOUT}/$<TARGET_FILE_NAME:InfiniFrame.Cef.Host>"
  VERBATIM)
if(INFINIFRAME_CEF_ADDRESS_SANITIZER)
  foreach(target InfiniFrame.Cef.Subprocess InfiniFrame.Cef.AsanAllocator)
    add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
      COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:${target}>"
        "${HOST_LAYOUT}/$<TARGET_FILE_NAME:${target}>" VERBATIM)
  endforeach()
endif()
if(EXISTS "${CEF_BINARY_DIR}/libminigbm.so")
  list(APPEND CEF_BINARY_FILES libminigbm.so)
endif()
foreach(file IN LISTS CEF_BINARY_FILES)
  # Linux deliberately relies on user namespaces, never installs a setuid helper.
  if(NOT file STREQUAL "chrome-sandbox")
    add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
      COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CEF_BINARY_DIR}/${file}" "${HOST_LAYOUT}/${file}" VERBATIM)
  endif()
endforeach()
foreach(file IN LISTS CEF_RESOURCE_FILES)
  if(IS_DIRECTORY "${CEF_RESOURCE_DIR}/${file}")
    add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
      COMMAND "${CMAKE_COMMAND}" -E copy_directory "${CEF_RESOURCE_DIR}/${file}" "${HOST_LAYOUT}/${file}" VERBATIM)
  else()
    add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
      COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CEF_RESOURCE_DIR}/${file}" "${HOST_LAYOUT}/${file}" VERBATIM)
  endif()
endforeach()
if(WIN32)
  add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CEF_BINARY_DIR}/bootstrapc.exe" "${HOST_LAYOUT}/InfiniFrame.Cef.Host.exe" VERBATIM)
endif()
foreach(file LICENSE.txt CREDITS.html README.txt)
  add_custom_command(TARGET InfiniFrame.Cef.Host POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CEF_ROOT}/${file}" "${HOST_LAYOUT}/${file}" VERBATIM)
endforeach()
if(WIN32)
  # Only the companion's own runtime payload, never a parent or system folder.
  set(CEF_TARGET_OUT_DIR "${HOST_LAYOUT}")
  SET_LPAC_ACLS(InfiniFrame.Cef.Host)
endif()
if(OS_LINUX)
  # DIRECTORY installation intentionally gives data files their normal 0644
  # mode. Install the executable separately so its mode does not depend on
  # permissions reported by the build filesystem (for example, DrvFS).
  if(INFINIFRAME_CEF_ADDRESS_SANITIZER)
    install(DIRECTORY "${CMAKE_BINARY_DIR}/runtime/$<CONFIG>/" DESTINATION "."
      PATTERN "InfiniFrame.Cef.Host" EXCLUDE PATTERN "InfiniFrame.Cef.Subprocess" EXCLUDE)
    install(PROGRAMS "$<TARGET_FILE:InfiniFrame.Cef.Subprocess>" DESTINATION ".")
  else()
    install(DIRECTORY "${CMAKE_BINARY_DIR}/runtime/$<CONFIG>/" DESTINATION "."
      PATTERN "InfiniFrame.Cef.Host" EXCLUDE)
  endif()
  install(PROGRAMS "$<TARGET_FILE:InfiniFrame.Cef.Host>" DESTINATION ".")
else()
  install(DIRECTORY "${CMAKE_BINARY_DIR}/runtime/$<CONFIG>/" DESTINATION ".")
endif()
# Standalone transport deadline tests: no CEF, built only on request, never installed into the payload.
add_executable(InfiniFrame.Cef.TransportTests EXCLUDE_FROM_ALL tests/transport_deadlines_test.cpp protocol.cpp)
target_compile_features(InfiniFrame.Cef.TransportTests PRIVATE cxx_std_20)
target_compile_definitions(InfiniFrame.Cef.TransportTests PRIVATE INFINIFRAME_CEF_TRANSPORT_TESTS=1)
set_target_properties(InfiniFrame.Cef.TransportTests PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/transport-tests/$<CONFIG>")
if(WIN32)
  target_link_libraries(InfiniFrame.Cef.TransportTests PRIVATE bcrypt)
  target_compile_options(InfiniFrame.Cef.TransportTests PRIVATE /EHsc)
else()
  find_package(Threads REQUIRED)
  target_link_libraries(InfiniFrame.Cef.TransportTests PRIVATE Threads::Threads)
endif()
if(WIN32)
  install(CODE [[
    execute_process(COMMAND icacls "${CMAKE_INSTALL_PREFIX}" /grant "*S-1-15-2-2:(OI)(CI)(RX)"
                    RESULT_VARIABLE acl_result OUTPUT_QUIET)
    if(NOT acl_result EQUAL 0)
      message(FATAL_ERROR "Could not grant LPAC read access to the installed companion payload.")
    endif()
  ]])
endif()
