# Release builds of the Celestron Origin test package, and trying them on OriginSimulator. See packaging/build-portable-deb.sh for what the build
# expects to be in place (the plugins' frontend, NINA/External) and NINA.Equipment/ARCHITECTURE.md for the simulator tests.
#
#   make release                  build the next origin<N> .deb in artifacts/ (N one past the newest v*-origin* tag; ITERATION=N overrides)
#   make release-test             unpack that .deb, run it against the simulator and drive it through the Advanced API (packaging/smoke-test-release.py)
#   make test                     the unit tests
#   make test-sim                 the tests that drive the simulator, which take tens of minutes at the real speeds (SIM_TESTS picks them)
#   make publish NOTES=file.md    tag the commit built and publish the .deb as a GitHub pre-release
#   make simulator / sim-start / sim-stop
#
# DOTNET_DIR names a .NET SDK not on PATH. SIM_DIR is a checkout of OriginSimulator (branch mount-model).

SHELL := bash
.SHELLFLAGS := -Eeuo pipefail -c
.ONESHELL:
.DELETE_ON_ERROR:

DOTNET_DIR ?=
ifneq ($(DOTNET_DIR),)
export PATH := $(DOTNET_DIR):$(PATH)
export DOTNET_ROOT := $(DOTNET_DIR)
endif

RUNTIME := linux-x64
VERSION := $(shell sed -n 's/.*AssemblyInformationalVersion("\([^"]*\)").*/\1/p' CommonAssemblyInfo.cs | head -n 1)
TAG_VERSION := $(firstword $(subst -, ,$(VERSION)))
LAST_ITERATION := $(shell git tag -l 'v*-origin*' | sed -n 's/.*-origin\([0-9][0-9]*\)$$/\1/p' | sort -n | tail -n 1)
ITERATION ?= $(shell echo $$(( $(or $(LAST_ITERATION),0) + 1 )))
DEB := artifacts/pins-origin_$(VERSION)+origin$(ITERATION)_amd64.deb
TAG := v$(TAG_VERSION)-origin$(ITERATION)
REPO ?= jrrk2/pins

SIM_DIR ?= $(HOME)/OriginSimulator
SIM_BIN := $(SIM_DIR)/build/OriginSimulator
QMAKE ?= $(firstword $(shell command -v qmake6 qmake))    # Qt 6: Qt 5's qmake compiles as C++11, and the simulator needs C++17
SIM_PORT ?= 18080
SIM_RUN := artifacts/sim
SIM_TESTS ?= OriginSimulatorIntegrationTest|OriginSimulatorModelBuilderTest

TRY := artifacts/release-test
# The unpacked package runs with a home of its own, so its profile and plugins do not touch this user's; the index files stay this user's.
INDEX_DIR ?= $(firstword $(wildcard $(HOME)/astrometry_indices /usr/share/astrometry /usr/local/share/astrometry))

TNS := NINA.Plugins/Touch-N-Stars/Touch-N-Stars
API := NINA.Plugins/ninaAPI/ninaAPI

.PHONY: release plugins release-test test test-sim publish simulator sim-start sim-stop

release: plugins
	packaging/build-portable-deb.sh $(ITERATION)

plugins:
	dotnet build $(API)/ninaAPI.csproj -c Release -r $(RUNTIME) -v q
	dotnet build $(TNS)/Touch-N-Stars.csproj -c Release -r $(RUNTIME) -v q

release-test: $(SIM_BIN)
	@[[ -f "$(DEB)" ]] || { echo "[ERROR] No $(DEB): make release first, or give ITERATION" >&2; exit 1; }
	[[ -n "$(INDEX_DIR)" ]] || { echo "[ERROR] No astrometry.net index files: give INDEX_DIR" >&2; exit 1; }
	rm -rf $(TRY); mkdir -p $(TRY)/home
	dpkg-deb -x $(DEB) $(TRY)/root
	$(MAKE) --no-print-directory sim-start
	trap '[[ -n "$${pins:-}" ]] && kill -- -$$pins 2>/dev/null; wait 2>/dev/null; $(MAKE) --no-print-directory sim-stop' EXIT
	echo "[INFO] Starting $(DEB) against the simulator; its log is $(TRY)/pins.log"
	HOME=$$PWD/$(TRY)/home ORIGIN_HOST=127.0.0.1:$(SIM_PORT) ORIGIN_ALLOW_DAYLIGHT=1 ORIGIN_INDEX_DIR=$(INDEX_DIR) \
	  setsid $(TRY)/root/usr/bin/pins > $(TRY)/pins.log 2>&1 & pins=$$!
	PINS_LOGS=$(TRY)/home/.local/share/NINA/Logs packaging/smoke-test-release.py

test:
	dotnet test NINA.Test/NINA.Test.csproj

# The simulator tests are [Explicit], so they run only when the filter names them.
test-sim: $(SIM_BIN)
	@$(MAKE) --no-print-directory sim-start
	trap '$(MAKE) --no-print-directory sim-stop' EXIT
	ORIGIN_SIM=127.0.0.1:$(SIM_PORT) dotnet test NINA.Test/NINA.Test.csproj --filter "$(subst |,|FullyQualifiedName~,FullyQualifiedName~$(SIM_TESTS))" \
	  --logger "console;verbosity=detailed"

publish:
	[[ -f "$(DEB)" && -f "$(DEB).sha256" ]] || { echo "[ERROR] No $(DEB): make release first, or give ITERATION" >&2; exit 1; }
	[[ -n "$(NOTES)" && -f "$(NOTES)" ]] || { echo "[ERROR] Give the release notes: make publish NOTES=file.md" >&2; exit 1; }
	[[ -z "$$(git status --porcelain --untracked-files=no)" ]] || { echo "[ERROR] Uncommitted changes: the tag would not be what was built" >&2; exit 1; }
	git tag $(TAG)
	git push origin $(TAG)
	gh release create $(TAG) -R $(REPO) --prerelease --title "PI.N.S. for Celestron Origin, test build origin$(ITERATION)" \
	  --notes-file $(NOTES) $(DEB) $(DEB).sha256

simulator: $(SIM_BIN)

$(SIM_BIN): $(wildcard $(SIM_DIR)/*.cpp $(SIM_DIR)/*.h $(SIM_DIR)/*.pro)
	[[ -f "$(SIM_DIR)/OriginSimulator.pro" ]] || { echo "[ERROR] No OriginSimulator in $(SIM_DIR): give SIM_DIR" >&2; exit 1; }
	mkdir -p $(SIM_DIR)/build
	cd $(SIM_DIR)/build
	$(QMAKE) ../OriginSimulator.pro
	$(MAKE) -j$$(nproc)

# Port 80 is refused by the tests, being where a real telescope listens; no discovery broadcast, in case one is on the network.
sim-start: $(SIM_BIN)
	@mkdir -p $(SIM_RUN)
	if [[ -f $(SIM_RUN)/pid ]] && kill -0 $$(cat $(SIM_RUN)/pid) 2>/dev/null; then echo "[INFO] The simulator is already running"; exit 0; fi
	(cd $(SIM_DIR) && ORIGIN_SIM_PORT=$(SIM_PORT) ORIGIN_SIM_BROADCAST=0 QT_QPA_PLATFORM=offscreen exec $(SIM_BIN)) > $(SIM_RUN)/sim.log 2>&1 &
	echo $$! > $(SIM_RUN)/pid
	for i in {1..30}; do (exec 3<>/dev/tcp/127.0.0.1/$(SIM_PORT)) 2>/dev/null && break; sleep 1; done
	(exec 3<>/dev/tcp/127.0.0.1/$(SIM_PORT)) 2>/dev/null || { echo "[ERROR] The simulator did not open port $(SIM_PORT); see $(SIM_RUN)/sim.log" >&2; exit 1; }
	echo "[INFO] The simulator is on port $(SIM_PORT); its log is $(SIM_RUN)/sim.log"

sim-stop:
	@if [[ -f $(SIM_RUN)/pid ]]; then kill $$(cat $(SIM_RUN)/pid) 2>/dev/null || true; rm -f $(SIM_RUN)/pid; fi
