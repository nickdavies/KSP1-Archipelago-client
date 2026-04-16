KSP_DIR     ?= $(HOME)/.local/share/Steam/steamapps/common/Kerbal Space Program
MOD_SUBDIR  ?= GameData/KSPArchipelago
OUT_DIR      = out/KSPArchipelago
BUILD_DIR    = KSPArchipelago/bin/Release/net40
LAUNCHER_DIR = $(KSP_DIR)/KSPLauncher_Data/Managed
LOG          = $(HOME)/workspaces/ksp_ap/ksp_stdout_stderr.log

.PHONY: all compile stage install run clean

all: stage

# Generate placeholder parts cfg directly into the staging directory.
$(OUT_DIR)/ap_placeholders.cfg: scripts/generate_placeholders.py
	mkdir -p $(OUT_DIR)
	python3 $< $@

# Compile the mod. dotnet handles incremental builds internally.
compile:
	dotnet build -c Release KSPArchipelago/KSPArchipelago.csproj

# Assemble the mod into out/KSPArchipelago.
stage: compile $(OUT_DIR)/ap_placeholders.cfg
	mkdir -p $(OUT_DIR)/Models
	cp $(BUILD_DIR)/KSPArchipelago.dll               $(OUT_DIR)/
	cp $(BUILD_DIR)/Archipelago.MultiClient.Net.dll  $(OUT_DIR)/
	cp $(BUILD_DIR)/Newtonsoft.Json.dll              $(OUT_DIR)/
	cp $(BUILD_DIR)/websocket-sharp.dll              $(OUT_DIR)/
	cp "$(LAUNCHER_DIR)/System.Numerics.dll"              $(OUT_DIR)/
	cp "$(LAUNCHER_DIR)/System.Runtime.Serialization.dll" $(OUT_DIR)/
	cp assets/ap_icon.png  $(OUT_DIR)/
	cp assets/Models/AP.mu $(OUT_DIR)/Models/

# Deploy staged output to KSP GameData.
install: stage
	rsync -a --delete $(OUT_DIR)/ "$(KSP_DIR)/$(MOD_SUBDIR)/"

# Build, install, then launch KSP.
run: install
	(sleep 15 && xdotool search --name "Kerbal Space Program" windowmap windowmove 100 100 windowfocus windowraise) & \
	"$(KSP_DIR)/KSP.x86_64" > $(LOG) 2>&1

clean:
	rm -rf out/ assets/ap_placeholders.cfg
	dotnet clean -c Release KSPArchipelago/KSPArchipelago.csproj
