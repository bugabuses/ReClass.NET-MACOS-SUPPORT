CXX = clang++
CC = clang
ARCH = -arch arm64
STD = -std=c++17
INC = -I../Dependencies/distorm/include
DEFS = -DRECLASSNET64=1
WARN = -Wall -Wno-deprecated-declarations
CXXFLAGS_COMMON = $(ARCH) $(STD) -fPIC $(WARN) $(DEFS) $(INC)
CFLAGS_COMMON = $(ARCH) -fPIC -Wall $(DEFS) $(INC)
LDFLAGS = $(ARCH) -dynamiclib -shared -Wl,-undefined,error

SRCS_CPP = WriteRemoteMemory.cpp ReadRemoteMemory.cpp OpenRemoteProcess.cpp IsProcessValid.cpp Input.cpp \
           EnumerateRemoteSectionsAndModules.cpp EnumerateProcesses.cpp DisassembleCode.cpp Debugger.cpp \
           ControlRemoteProcess.cpp CloseRemoteProcess.cpp TaskPorts.cpp
SHARED_CPP = ../Shared/DistormHelper.cpp
DISTORM_C = decoder.c distorm.c instructions.c insts.c mnemonics.c operands.c prefix.c textdefs.c

define BUILD_RULES
OBJDIR_$(1) = obj/$(1)
OUTDIR_$(1) = build/$(1)
OUT_$(1) = $$(OUTDIR_$(1))/NativeCore.dylib
OBJS_$(1) = $$(addprefix $$(OBJDIR_$(1))/,$$(SRCS_CPP:.cpp=.o)) \
            $$(OBJDIR_$(1))/DistormHelper.o \
            $$(addprefix $$(OBJDIR_$(1))/,$$(DISTORM_C:.c=.o))

$(1): $$(OUT_$(1))

$$(OUT_$(1)): $$(OBJS_$(1))
	@mkdir -p $$(OUTDIR_$(1))
	$$(CXX) $$(LDFLAGS) -o $$@ $$^

$$(OBJDIR_$(1))/%.o: %.cpp NativeCore.hpp
	@mkdir -p $$(OBJDIR_$(1))
	$$(CXX) $$(CXXFLAGS_COMMON) $(2) -c $$< -o $$@

$$(OBJDIR_$(1))/DistormHelper.o: ../Shared/DistormHelper.cpp
	@mkdir -p $$(OBJDIR_$(1))
	$$(CXX) $$(CXXFLAGS_COMMON) $(2) -c $$< -o $$@

$$(OBJDIR_$(1))/%.o: ../Dependencies/distorm/src/%.c
	@mkdir -p $$(OBJDIR_$(1))
	$$(CC) $$(CFLAGS_COMMON) $(2) -c $$< -o $$@

clean_$(1):
	rm -rf $$(OBJDIR_$(1)) $$(OUTDIR_$(1))
endef

$(eval $(call BUILD_RULES,debug,-g -O0))
$(eval $(call BUILD_RULES,release,-O2))

all: debug release

clean: clean_debug clean_release
	rm -rf obj build test/harness

test: debug test/harness

test/harness: test/harness.cpp
	$(CXX) $(ARCH) $(STD) -Wall $(DEFS) -o $@ $<

.PHONY: all clean debug release clean_debug clean_release test
