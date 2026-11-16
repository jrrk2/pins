# Release builds of the Celestron Origin test package, and trying them on OriginSimulator, from a clone of this repository: what comes from elsewhere
# is pinned in packaging/deps.env and fetched by packaging/fetch-deps.sh (see BUILD_PINS.md). NINA.Equipment/ARCHITECTURE.md describes the simulator tests.
#
#   make deps                     fetch and build what a release needs from outside this repository; make release does this first
#   make release                  build the next origin<N> package in artifacts/ (N one past the newest v*-origin* tag; ITERATION=N overrides):
#                                 a .deb on Linux (amd64 or arm64), a .tar.gz on a Mac
#   make release-test             unpack that package, run it against the simulator and drive it through the Advanced API (packaging/smoke-test-release.py)
#   make test                     the unit tests
#   make test-sim                 the tests that drive the simulator, which take tens of minutes at the real speeds (SIM_TESTS picks them)
#   make publish                  tag the commit the package was built from and publish it as a GitHub pre-release (or add it to the release
#                                 another machine published), with the notes in
#                                 packaging/release-notes/origin<N>.md (NOTES=file overrides)
#   make pi-image                 a Raspberry Pi OS Lite image for Raspberry Pi Imager carrying the arm64 package (DEB=file overrides),
#                                 which its first boot installs (packaging/build-pi-image.sh); needs no root, so it builds on any Linux
#   make simulator / sim-start / sim-stop
#
# DOTNET_DIR names a .NET SDK to use (default .tools/dotnet, where make deps installs one when the machine has none). SIM_DIR names a checkout of
# OriginSimulator to use instead of the pinned one.

ifeq ($(filter oneshell,$(.FEATURES)),)
$(error This Makefile needs GNU make 3.82 or later; macOS's make is 3.81: brew install make, and run gmake)
endif

SHELL := bash
.SHELLFLAGS := -Eeuo pipefail -c
.ONESHELL:
.DELETE_ON_ERROR:

DOTNET_DIR ?= $(CURDIR)/.tools/dotnet
# Recursive, so that an SDK that make deps installs during this run is used
DOTNET_ENV = $(if $(wildcard $(DOTNET_DIR)/dotnet),PATH="$(DOTNET_DIR):$$PATH" DOTNET_ROOT="$(DOTNET_DIR)")

ifeq ($(shell uname -s),Darwin)
RUNTIME := osx-$(if $(filter arm64,$(shell uname -m)),arm64,x64)
else
RUNTIME := linux-$(if $(filter aarch64 arm64,$(shell uname -m)),arm64,x64)
endif
VERSION := $(shell sed -n 's/.*AssemblyInformationalVersion("\([^"]*\)").*/\1/p' CommonAssemblyInfo.cs | head -n 1)
TAG_VERSION := $(firstword $(subst -, ,$(VERSION)))
LAST_ITERATION := $(shell git tag -l 'v*-origin*' | sed -n 's/.*-origin\([0-9][0-9]*\)$$/\1/p' | sort -n | tail -n 1)
ITERATION ?= $(shell echo $$(( $(or $(LAST_ITERATION),0) + 1 )))
ifneq ($(filter linux-%,$(RUNTIME)),)
PACKAGE := artifacts/pins-origin_$(VERSION)+origin$(ITERATION)_$(shell dpkg --print-architecture).deb
PACKAGER := packaging/build-portable-deb.sh
SHA256SUM := sha256sum
# The package's own launcher, and where its logs go, once unpacked into $(TRY)
UNPACK = dpkg-deb -x $(PACKAGE) $(TRY)/root
LAUNCHER = $(TRY)/root/usr/bin/pins
TRY_LOGS = $(TRY)/home/.local/share/NINA/Logs
SETSID := setsid
else
PACKAGE := artifacts/pins-origin_$(VERSION)+origin$(ITERATION)_macos-$(subst osx-,,$(RUNTIME)).tar.gz
PACKAGER := packaging/build-macos-package.sh
SHA256SUM := shasum -a 256
UNPACK = mkdir -p $(TRY)/root && tar -xzf $(PACKAGE) -C $(TRY)/root
LAUNCHER = $(TRY)/root/pins/pins
TRY_LOGS = $(TRY)/home/Library/Application Support/NINA/Logs
# macOS has no setsid
SETSID := perl -e 'setpgrp(0, 0); exec @ARGV' --
endif
TAG := v$(TAG_VERSION)-origin$(ITERATION)
REPO ?= jrrk2/pins
NOTES ?= packaging/release-notes/origin$(ITERATION).md

SIM_PINNED := $(CURDIR)/artifacts/src/OriginSimulator
SIM_DIR ?= $(SIM_PINNED)
# qmake builds an application bundle on macOS
SIM_BIN := $(SIM_DIR)/build/$(if $(filter osx-%,$(RUNTIME)),OriginSimulator.app/Contents/MacOS/OriginSimulator,OriginSimulator)
QMAKE ?= $(firstword $(shell command -v qmake6 qmake))    # Qt 6: Qt 5's qmake compiles as C++11, and the simulator needs C++17
SIM_PORT ?= 18080
SIM_RUN := artifacts/sim
SIM_TESTS ?= OriginSimulatorIntegrationTest|OriginSimulatorModelBuilderTest

TRY := artifacts/release-test
# The unpacked package runs with a home of its own, so its profile and plugins do not touch this user's; the index files stay this user's.
# The first folder with index files for the Origin's field (4208-4211, or Debian's index-2mass-08 to 11)
INDEX_DIRS := $(HOME)/astrometry_indices /usr/share/astrometry /usr/local/share/astrometry /opt/homebrew/share/astrometry /opt/local/share/astrometry
INDEX_DIR ?= $(patsubst %/,%,$(dir $(firstword $(foreach d,$(INDEX_DIRS),$(wildcard $(d)/index-420[89]*.fits $(d)/index-421[01]*.fits $(d)/index-2mass-0[89].fits $(d)/index-2mass-1[01].fits)))))

TNS := NINA.Plugins/Touch-N-Stars/Touch-N-Stars
API := NINA.Plugins/ninaAPI/ninaAPI

.PHONY: deps release plugins release-test test test-sim publish pi-image simulator sim-build sim-start sim-stop

deps:
	packaging/fetch-deps.sh build

# The commit built goes next to the package, for make publish to tag (marked dirty if tracked files had changes).
release: plugins
	built="$$(git rev-parse HEAD)$$([[ -z "$$(git status --porcelain --untracked-files=no)" ]] || echo -dirty)"
	$(DOTNET_ENV) $(PACKAGER) $(ITERATION)
	echo "$$built" > $(PACKAGE).commit

# PinsRoot: the Touch-N-Stars plugin builds against this repository rather than N.I.N.A.'s Windows packages, as it does by itself only on Linux.
plugins: deps
	$(DOTNET_ENV) dotnet build $(API)/ninaAPI.csproj -c Release -r $(RUNTIME) -v q
	$(DOTNET_ENV) dotnet build $(TNS)/Touch-N-Stars.csproj -c Release -r $(RUNTIME) -p:PinsRoot=$(CURDIR) -v q

release-test:
	@[[ -f "$(PACKAGE)" ]] || { echo "[ERROR] No $(PACKAGE): make release first, or give ITERATION" >&2; exit 1; }
	[[ -n "$(INDEX_DIR)" ]] || { echo "[ERROR] No astrometry.net index files for the Origin's field (4208-4211): give INDEX_DIR" >&2; exit 1; }
	rm -rf $(TRY); mkdir -p $(TRY)/home
	$(UNPACK)
	$(MAKE) --no-print-directory sim-start
	trap '[[ -n "$${pins:-}" ]] && kill -- -$$pins 2>/dev/null; wait 2>/dev/null; $(MAKE) --no-print-directory sim-stop' EXIT
	echo "[INFO] Starting $(PACKAGE) against the simulator; its log is $(TRY)/pins.log"
	HOME=$$PWD/$(TRY)/home ORIGIN_HOST=127.0.0.1:$(SIM_PORT) ORIGIN_ALLOW_DAYLIGHT=1 ORIGIN_INDEX_DIR=$(INDEX_DIR) \
	  $(SETSID) $(LAUNCHER) > $(TRY)/pins.log 2>&1 & pins=$$!
	PINS_LOGS="$(TRY_LOGS)" packaging/smoke-test-release.py

test:
	$(DOTNET_ENV) dotnet test NINA.Test/NINA.Test.csproj

# The simulator tests are [Explicit], so they run only when the filter names them.
test-sim:
	@$(MAKE) --no-print-directory sim-start
	trap '$(MAKE) --no-print-directory sim-stop' EXIT
	ORIGIN_SIM=127.0.0.1:$(SIM_PORT) $(DOTNET_ENV) dotnet test NINA.Test/NINA.Test.csproj --filter "$(subst |,|FullyQualifiedName~,FullyQualifiedName~$(SIM_TESTS))" \
	  --logger "console;verbosity=detailed"

# The package may be built on another machine and copied here with its .sha256 and .commit. Commits since the build may change only the release notes.
DEB ?= artifacts/pins-origin_$(VERSION)+origin$(ITERATION)_arm64.deb

pi-image:
	packaging/build-pi-image.sh $(DEB)

publish:
	for f in $(PACKAGE) $(PACKAGE).sha256 $(PACKAGE).commit; do
	  [[ -f "$$f" ]] || { echo "[ERROR] No $$f: make release first, or give ITERATION" >&2; exit 1; }
	done
	(cd $(dir $(PACKAGE)) && $(SHA256SUM) --quiet -c $(notdir $(PACKAGE)).sha256)
	built=$$(cat $(PACKAGE).commit)
	[[ "$$built" != *-dirty ]] || { echo "[ERROR] $(PACKAGE) was built with uncommitted changes" >&2; exit 1; }
	git merge-base --is-ancestor $$built HEAD || { echo "[ERROR] $(PACKAGE) was built from $$built, which this branch does not contain" >&2; exit 1; }
	git diff --quiet $$built HEAD -- . ':(exclude)packaging/release-notes' \
	  || { echo "[ERROR] Commits since $(PACKAGE) was built ($$built) change more than release notes: make release again" >&2; exit 1; }
	[[ -f "$(NOTES)" ]] || { echo "[ERROR] No release notes $(NOTES): write them, or give NOTES=file" >&2; exit 1; }
	git branch -r --contains $$built | grep -q . || { echo "[ERROR] Push $$built to GitHub first" >&2; exit 1; }
	if git rev-parse -q --verify refs/tags/$(TAG) >/dev/null; then
	  [[ "$$(git rev-list -n 1 $(TAG))" == "$$built" ]] || { echo "[ERROR] Tag $(TAG) exists on another commit" >&2; exit 1; }
	else
	  git tag $(TAG) $$built
	fi
	git push origin $(TAG)
	if gh release view $(TAG) -R $(REPO) > /dev/null 2>&1; then
	  echo "[INFO] $(TAG) is published already: adding $(PACKAGE) to it"
	  gh release upload $(TAG) -R $(REPO) $(PACKAGE) $(PACKAGE).sha256
	else
	  gh release create $(TAG) -R $(REPO) --prerelease --verify-tag --title "PI.N.S. for Celestron Origin, test build origin$(ITERATION)" \
	    --notes-file $(NOTES) $(PACKAGE) $(PACKAGE).sha256
	fi

# The pinned simulator is fetched, or moved to the pin, first; the build then sees its sources as they are.
simulator:
	@if [[ "$(SIM_DIR)" == "$(SIM_PINNED)" ]]; then INDEX_DIR="$(INDEX_DIR)" packaging/fetch-deps.sh sim; fi
	$(MAKE) --no-print-directory sim-build

sim-build: $(SIM_BIN)

$(SIM_BIN): $(wildcard $(SIM_DIR)/*.cpp $(SIM_DIR)/*.h $(SIM_DIR)/*.pro)
	[[ -f "$(SIM_DIR)/OriginSimulator.pro" ]] || { echo "[ERROR] No OriginSimulator in $(SIM_DIR): give SIM_DIR" >&2; exit 1; }
	mkdir -p $(SIM_DIR)/build
	cd $(SIM_DIR)/build
	$(QMAKE) ../OriginSimulator.pro
	$(MAKE) -j$$(nproc)

# Port 80 is refused by the tests, being where a real telescope listens; no discovery broadcast, in case one is on the network.
sim-start: simulator
	@mkdir -p $(SIM_RUN)
	if [[ -f $(SIM_RUN)/pid ]] && kill -0 $$(cat $(SIM_RUN)/pid) 2>/dev/null; then echo "[INFO] The simulator is already running"; exit 0; fi
	(cd $(SIM_DIR) && ORIGIN_SIM_PORT=$(SIM_PORT) ORIGIN_SIM_BROADCAST=0 QT_QPA_PLATFORM=offscreen exec $(SIM_BIN)) > $(SIM_RUN)/sim.log 2>&1 &
	echo $$! > $(SIM_RUN)/pid
	for i in {1..30}; do (exec 3<>/dev/tcp/127.0.0.1/$(SIM_PORT)) 2>/dev/null && break; sleep 1; done
	(exec 3<>/dev/tcp/127.0.0.1/$(SIM_PORT)) 2>/dev/null || { echo "[ERROR] The simulator did not open port $(SIM_PORT); see $(SIM_RUN)/sim.log" >&2; exit 1; }
	echo "[INFO] The simulator is on port $(SIM_PORT); its log is $(SIM_RUN)/sim.log"

sim-stop:
	@if [[ -f $(SIM_RUN)/pid ]]; then kill $$(cat $(SIM_RUN)/pid) 2>/dev/null || true; rm -f $(SIM_RUN)/pid; fi
