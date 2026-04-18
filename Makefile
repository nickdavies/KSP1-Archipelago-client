# Where KSP is installed (for deploy + run only)
KSP_DIR      ?= $(HOME)/.local/share/Steam/steamapps/common/Kerbal Space Program
MOD_SUBDIR   ?= GameData/KSPArchipelago
# Stripped reference assemblies for compilation (downloaded by `make deps`)
STUBS_DIR     = lib/ksp-stubs
OUT_DIR       = out/KSPArchipelago
BUILD_DIR     = KSPArchipelago/bin/Release/net40
LOG           = $(HOME)/workspaces/ksp_ap/ksp_stdout_stderr.log

.PHONY: all compile stage install run clean deps

all: stage

# Download KSP stripped reference assemblies for compilation.
deps:
	mkdir -p $(STUBS_DIR)
	curl -sL https://github.com/KSPModdingLibs/KSPLibs/raw/main/KSP-1.12.5.zip -o /tmp/ksp-libs.zip
	unzip -qo /tmp/ksp-libs.zip -d $(STUBS_DIR)
	ln -sfn KSP_x64_Data $(STUBS_DIR)/KSP_Data
	rm -f /tmp/ksp-libs.zip

# Generate placeholder parts cfg directly into the staging directory.
$(OUT_DIR)/ap_placeholders.cfg: scripts/generate_placeholders.py
	mkdir -p $(OUT_DIR)
	python3 $< $@

# Compile the mod using stripped reference assemblies.
compile:
	@test -d $(STUBS_DIR)/KSP_Data || { echo "Run 'make deps' first to download KSP reference assemblies"; exit 1; }
	dotnet build -c Release -p:KspDir="$(CURDIR)/$(STUBS_DIR)" KSPArchipelago/KSPArchipelago.csproj

# Assemble the mod into out/KSPArchipelago.
stage: compile $(OUT_DIR)/ap_placeholders.cfg
	mkdir -p $(OUT_DIR)/Models
	cp $(BUILD_DIR)/KSPArchipelago.dll               $(OUT_DIR)/
	cp $(BUILD_DIR)/Archipelago.MultiClient.Net.dll  $(OUT_DIR)/
	cp $(BUILD_DIR)/Newtonsoft.Json.dll              $(OUT_DIR)/
	cp $(BUILD_DIR)/websocket-sharp.dll              $(OUT_DIR)/
	cp lib/System.Numerics.dll                       $(OUT_DIR)/
	cp lib/System.Runtime.Serialization.dll          $(OUT_DIR)/
	cp assets/ap_icon.png  $(OUT_DIR)/
	cp assets/Models/AP.mu $(OUT_DIR)/Models/

# Deploy staged output to KSP GameData.
install: stage
	rsync -a --delete $(OUT_DIR)/ "$(KSP_DIR)/$(MOD_SUBDIR)/"

# Build, install, then launch KSP.
run: install
	(sleep 15 && export KSP_ID=$$(xdotool search --class "KSP.x86_64") && xdotool windowmap --sync $$KSP_ID && xdotool windowmove $$KSP_ID 100 100 && xdotool windowfocus $$KSP_ID && xdotool windowraise $$KSP_ID) & \
	"$(KSP_DIR)/KSP.x86_64" > $(LOG) 2>&1

clean:
	rm -rf out/ assets/ap_placeholders.cfg
	dotnet clean -c Release KSPArchipelago/KSPArchipelago.csproj
