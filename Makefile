# Where KSP is installed (for deploy + run only)
KSP_DIR        ?= $(HOME)/.local/share/Steam/steamapps/common/Kerbal Space Program
MOD_SUBDIR     ?= GameData/KSPArchipelago
# Stripped reference assemblies for compilation (downloaded by `make deps`)
STUBS_DIR       = lib/ksp-stubs
OUT_DIR         = out/KSPArchipelago
BUILD_DIR_MAIN  = KSPArchipelago/bin/Release/net40
BUILD_DIR_KSC   = KSPArchipelago.KSC/bin/Release/net48
LOG             = $(HOME)/workspaces/ksp_ap/ksp_stdout_stderr.log

# Build version stamped into the assemblies (read at runtime by ModVersion:
# logged to KSP.log and shown in the in-game menu title). Derived from the git
# tag; CI overrides via `make stage VERSION=<tag>`. A leading "v" is stripped so
# the v0.4.3 tag yields "0.4.3"; unstamped/untagged trees fall back to "dev".
VERSION        ?= $(patsubst v%,%,$(shell git describe --tags --always --dirty 2>/dev/null || echo dev))

.PHONY: all compile compile-main compile-ksc stage install run clean deps

all: stage

# Pinned KerbalKonstructs release used as a compile-time reference. Must be
# the KSP-RO fork — the older GER-Space fork is missing types we use.
KK_VERSION      = v1.12.2.0
KK_ZIP_URL      = https://github.com/KSP-RO/Kerbal-Konstructs/releases/download/$(KK_VERSION)/KerbalKonstructs-$(KK_VERSION).zip

# Download KSP stripped reference assemblies + KK dll for compilation.
deps:
	mkdir -p $(STUBS_DIR)
	curl -sL https://github.com/KSPModdingLibs/KSPLibs/raw/main/KSP-1.12.5.zip -o /tmp/ksp-libs.zip
	unzip -qo /tmp/ksp-libs.zip -d $(STUBS_DIR)
	ln -sfn KSP_x64_Data $(STUBS_DIR)/KSP_Data
	rm -f /tmp/ksp-libs.zip
	curl -sL $(KK_ZIP_URL) -o /tmp/kk.zip
	unzip -qo /tmp/kk.zip "GameData/KerbalKonstructs/KerbalKonstructs.dll" -d $(STUBS_DIR)
	rm -f /tmp/kk.zip

# Generate placeholder parts cfg directly into the staging directory.
$(OUT_DIR)/ap_placeholders.cfg: scripts/generate_placeholders.py
	mkdir -p $(OUT_DIR)
	python3 $< $@

# Compile the main KK-free mod using stripped reference assemblies.
compile-main:
	@test -d $(STUBS_DIR)/KSP_Data || { echo "Run 'make deps' first to download KSP reference assemblies"; exit 1; }
	dotnet build -c Release -p:KspDir="$(CURDIR)/$(STUBS_DIR)" -p:InformationalVersion="$(VERSION)" KSPArchipelago/KSPArchipelago.csproj

# Compile the KK-dependent selector sub-assembly. Both KK and the Unity
# refs resolve against the stubs tree — `make deps` drops a KerbalKonstructs.dll
# into $(STUBS_DIR)/GameData/KerbalKonstructs/ so this works in CI with no
# KSP install. Project-references the main csproj so the IStartingBodyHandler
# interface is shared, not duplicated.
compile-ksc: compile-main
	dotnet build -c Release -p:KspDir="$(CURDIR)/$(STUBS_DIR)" -p:InformationalVersion="$(VERSION)" KSPArchipelago.KSC/KSPArchipelago.KSC.csproj

compile: compile-main compile-ksc

# Assemble the mod into out/KSPArchipelago.
stage: compile $(OUT_DIR)/ap_placeholders.cfg
	mkdir -p $(OUT_DIR)/Models $(OUT_DIR)/Heightmaps $(OUT_DIR)/FlagsAgency
	cp $(BUILD_DIR_MAIN)/KSPArchipelago.dll               $(OUT_DIR)/
	cp $(BUILD_DIR_MAIN)/Archipelago.MultiClient.Net.dll  $(OUT_DIR)/
	cp $(BUILD_DIR_MAIN)/Newtonsoft.Json.dll              $(OUT_DIR)/
	cp $(BUILD_DIR_MAIN)/websocket-sharp.dll              $(OUT_DIR)/
	cp lib/System.Numerics.dll                            $(OUT_DIR)/
	cp lib/System.Runtime.Serialization.dll               $(OUT_DIR)/
	cp assets/ap_icon.png  $(OUT_DIR)/
	cp assets/ap_icon_gray.png  $(OUT_DIR)/
	cp assets/Models/AP.mu $(OUT_DIR)/Models/
	cp assets/Flags/*.png $(OUT_DIR)/FlagsAgency/   # AP flags -> browser "Agency" tab
	cp $(BUILD_DIR_KSC)/KSPArchipelago.KSC.dll            $(OUT_DIR)/
	cp KSPArchipelago.KSC/Heightmaps/APKSC_KerbinCurve.png $(OUT_DIR)/Heightmaps/
	cp KSPArchipelago.KSC/Heightmaps/APKSC_KerbinCurve.cfg $(OUT_DIR)/Heightmaps/

# Deploy staged output to KSP GameData.
install: stage
	rsync -a --delete $(OUT_DIR)/ "$(KSP_DIR)/$(MOD_SUBDIR)/"

# Build, install, then launch KSP.
run: install
	(sleep 15 && export KSP_ID=$$(xdotool search --class "KSP.x86_64") && xdotool windowmap --sync $$KSP_ID && xdotool windowmove $$KSP_ID 100 100 && xdotool windowfocus $$KSP_ID && xdotool windowraise $$KSP_ID) & \
	"$(KSP_DIR)/KSP.x86_64" > $(LOG) 2>&1

run-without-install:
	(sleep 15 && export KSP_ID=$$(xdotool search --class "KSP.x86_64") && xdotool windowmap --sync $$KSP_ID && xdotool windowmove $$KSP_ID 100 100 && xdotool windowfocus $$KSP_ID && xdotool windowraise $$KSP_ID) & \
	"$(KSP_DIR)/KSP.x86_64" > $(LOG) 2>&1

clean:
	rm -rf out/ assets/ap_placeholders.cfg
	dotnet clean -c Release KSPArchipelago/KSPArchipelago.csproj
	dotnet clean -c Release KSPArchipelago.KSC/KSPArchipelago.KSC.csproj
