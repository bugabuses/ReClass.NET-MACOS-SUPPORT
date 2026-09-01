.PHONY: all clean debug clean_debug release clean_release update docker_all docker_debug docker_release podman_all podman_debug podman_release dist

all: debug release dist

clean: clean_debug clean_release

debug:
	cd ReClass.NET_Launcher && make debug
	cd ReClass.NET && make debug
	cd NativeCore/Unix && make debug

clean_debug:
	cd ReClass.NET_Launcher && make clean_debug
	cd ReClass.NET && make clean_debug
	cd NativeCore/Unix && make clean_debug
	rm -rf build/Debug

release:
	cd ReClass.NET_Launcher && make release
	cd ReClass.NET && make release
	cd NativeCore/Unix && make release

clean_release:
	cd ReClass.NET_Launcher && make clean_release
	cd ReClass.NET && make clean_release
	cd NativeCore/Unix && make clean_release
	rm -rf build/Release

update:
	cd ReClass.NET && make update

docker_all:
	make docker_debug
	make docker_release
	make dist

docker_debug:
	cd ReClass.NET_Launcher && make docker_debug
	cd ReClass.NET && make docker_debug
	docker container run --rm -v ${PWD}:/build:z -w /build -u $(shell id -u ${USER}):$(shell id -g ${USER}) gcc_multilib:latest bash -c "cd NativeCore/Unix && make debug"

docker_release:
	cd ReClass.NET_Launcher && make docker_release
	cd ReClass.NET && make docker_release
	docker container run --rm -v ${PWD}:/build:z -w /build -u $(shell id -u ${USER}):$(shell id -g ${USER}) gcc_multilib:latest bash -c "cd NativeCore/Unix && make release"

podman_all:
	make podman_debug
	make podman_release
	make dist

podman_debug:
	cd ReClass.NET_Launcher && make podman_debug
	cd ReClass.NET && make podman_debug
	podman container run --rm -v ${PWD}:/build:z -w /build gcc_multilib:latest bash -c "cd NativeCore/Unix && make debug"

podman_release:
	cd ReClass.NET_Launcher && make podman_release
	cd ReClass.NET && make podman_release
	podman container run --rm -v ${PWD}:/build:z -w /build gcc_multilib:latest bash -c "cd NativeCore/Unix && make release"

dist:
	test -d build || mkdir -p build
	cp -r ReClass.NET/bin/* build/
	cp -r ReClass.NET_Launcher/bin/* build/
	cp NativeCore/Unix/build/debug/x86/NativeCore.so build/Debug/x86
	cp NativeCore/Unix/build/debug/x64/NativeCore.so build/Debug/x64
	cp NativeCore/Unix/build/release/x86/NativeCore.so build/Release/x86
	cp NativeCore/Unix/build/release/x64/NativeCore.so build/Release/x64
	test -d build/Debug/x86/Plugins || mkdir build/Debug/x86/Plugins
	test -d build/Debug/x64/Plugins || mkdir build/Debug/x64/Plugins
	test -d build/Release/x86/Plugins || mkdir build/Release/x86/Plugins
	test -d build/Release/x64/Plugins || mkdir build/Release/x64/Plugins
	test -d build/Debug/x86 && cp -r Dependencies/x86/* build/Debug/x86
	test -d build/Debug/x64 && cp -r Dependencies/x64/* build/Debug/x64
	test -d build/Release/x86 && cp -r Dependencies/x86/* build/Release/x86
	test -d build/Release/x64 && cp -r Dependencies/x64/* build/Release/x64

# ---- macOS (Mono + XQuartz, arm64) ----
MACOS_XBUILD = DYLD_LIBRARY_PATH=/opt/homebrew/lib xbuild /p:Platform=x64 /nologo /verbosity:minimal
MACOS_XBUILD_ANYCPU = DYLD_LIBRARY_PATH=/opt/homebrew/lib xbuild /nologo /verbosity:minimal

macos_update:
	mono Dependencies/nuget.exe restore ReClass.NET.sln

macos_debug:
	$(MACOS_XBUILD_ANYCPU) /p:Configuration=Debug ReClass.NET_Launcher/ReClass.NET_Launcher.csproj
	$(MACOS_XBUILD) /p:Configuration=Debug ReClass.NET/ReClass.NET.csproj
	$(MAKE) -C NativeCore/MacOS debug
	$(MAKE) macos_dist_debug

macos_release:
	$(MACOS_XBUILD_ANYCPU) /p:Configuration=Release ReClass.NET_Launcher/ReClass.NET_Launcher.csproj
	$(MACOS_XBUILD) /p:Configuration=Release ReClass.NET/ReClass.NET.csproj
	$(MAKE) -C NativeCore/MacOS release
	$(MAKE) macos_dist_release

macos_dist_debug:
	mkdir -p build/Debug/x64/Plugins
	cp -r ReClass.NET/bin/Debug/x64/* build/Debug/x64/
	-cp ReClass.NET_Launcher/bin/Debug/ReClass.NET_Launcher.exe build/Debug/x64/ 2>/dev/null
	cp NativeCore/MacOS/build/debug/NativeCore.dylib build/Debug/x64/
	cp -r Dependencies/x64/* build/Debug/x64/ 2>/dev/null || true

macos_dist_release:
	mkdir -p build/Release/x64/Plugins
	cp -r ReClass.NET/bin/Release/x64/* build/Release/x64/
	-cp ReClass.NET_Launcher/bin/Release/ReClass.NET_Launcher.exe build/Release/x64/ 2>/dev/null
	cp NativeCore/MacOS/build/release/NativeCore.dylib build/Release/x64/
	cp -r Dependencies/x64/* build/Release/x64/ 2>/dev/null || true

macos: macos_release

macos_clean:
	rm -rf ReClass.NET/bin ReClass.NET/obj ReClass.NET_Launcher/bin ReClass.NET_Launcher/obj build
	$(MAKE) -C NativeCore/MacOS clean

.PHONY: macos macos_update macos_debug macos_release macos_dist_debug macos_dist_release macos_clean
