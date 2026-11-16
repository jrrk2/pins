# Release builds of the Celestron Origin test package, and trying them on OriginSimulator, from a clone of this repository: what comes from elsewhere
# is pinned in packaging/deps.env and fetched by packaging/fetch-deps.sh (see BUILD_PINS.md). NINA.Equipment/ARCHITECTURE.md describes the simulator tests.
#
#   make deps                     fetch and build what a release needs from outside this repository; make release does this first
#   make release                  build the next origin<N> .deb in artifacts/ (N one past the newest v*-origin* tag; ITERATION=N overrides)
#   make release-test             unpack that .deb, run it against the simulator and drive it through the Advanced API (packaging/smoke-test-release.py)
#   make test                     the unit tests
#   make test-sim                 the tests that drive the simulator, which take tens of minutes at the real speeds (SIM_TESTS picks them)
#   make publish                  tag the commit the .deb was built from and publish it as a GitHub pre-release, with the notes in
#                                 packaging/release-notes/origin<N>.md (NOTES=file overrides)
#   make simulator / sim-start / sim-stop
#
# DOTNET_DIR names a .NET SDK to use (default .tools/dotnet, where make deps installs one when the machine has none). SIM_DIR names a checkout of
# OriginSimulator to use instead of the pinned one.

SHELL := bash
.SHELLFLAGS := -Eeuo pipefail -c
.ONESHELL:
.DELETE_ON_ERROR:

DOTNET_DIR ?= $(CURDIR)/.tools/dotnet
# Recursive, so that an SDK that make deps installs during this run is used
DOTNET_ENV = $(if $(wildcard $(DOTNET_DIR)/dotnet),PATH="$(DOTNET_DIR):$$PATH" DOTNET_ROOT="$(DOTNET_DIR)")

RUNTIME := linux-x64
VERSION := $(shell sed -n 's/.*AssemblyInformationalVersion("\([^"]*\)").*/\1/p' CommonAssemblyInfo.cs | head -n 1)
TAG_VERSION := $(firstword $(subst -, ,$(VERSION)))
LAST_ITERATION := $(shell git tag -l 'v*-origin*' | sed -n 's/.*-origin\([0-9][0-9]*\)$$/\1/p' | sort -n | tail -n 1)
ITERATION ?= $(shell echo $$(( $(or $(LAST_ITERATION),0) + 1 )))
DEB := artifacts/pins-origin_$(VERSION)+origin$(ITERATION)_amd64.deb
TAG := v$(TAG_VERSION)-origin$(ITERATION)
REPO ?= jrrk2/pins
NOTES ?= packaging/release-notes/origin$(ITERATION).md
# The package is for Ubuntu 24.04 and later, and Debian 13: a .deb that needs a newer C library than 24.04's was built on too new a system.
OLDEST_GLIBC ?= 2.39

SIM_PINNED := $(CURDIR)/artifacts/src/OriginSimulator
SIM_DIR ?= $(SIM_PINNED)
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

.PHONY: deps release plugins release-test test test-sim publish simulator sim-build sim-start sim-stop

deps:
	packaging/fetch-deps.sh build

# The commit built goes next to the .deb, for make publish to tag (marked dirty if tracked files had changes).
release: plugins
	$(DOTNET_ENV) packaging/build-portable-deb.sh $(ITERATION)
	echo "$$(git rev-parse HEAD)$$([[ -z "$$(git status --porcelain --untracked-files=no)" ]] || echo -dirty)" > $(DEB).commit

plugins: deps
	$(DOTNET_ENV) dotnet build $(API)/ninaAPI.csproj -c Release -r $(RUNTIME) -v q
	$(DOTNET_ENV) dotnet build $(TNS)/Touch-N-Stars.csproj -c Release -r $(RUNTIME) -v q

release-test:
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
	$(DOTNET_ENV) dotnet test NINA.Test/NINA.Test.csproj

# The simulator tests are [Explicit], so they run only when the filter names them.
test-sim:
	@$(MAKE) --no-print-directory sim-start
	trap '$(MAKE) --no-print-directory sim-stop' EXIT
	ORIGIN_SIM=127.0.0.1:$(SIM_PORT) $(DOTNET_ENV) dotnet test NINA.Test/NINA.Test.csproj --filter "$(subst |,|FullyQualifiedName~,FullyQualifiedName~$(SIM_TESTS))" \
	  --logger "console;verbosity=detailed"

# The .deb may be built on another machine and copied here with its .sha256 and .commit. Commits since the build may change only the release notes.
publish:
	for f in $(DEB) $(DEB).sha256 $(DEB).commit; do
	  [[ -f "$$f" ]] || { echo "[ERROR] No $$f: make release first (on Ubuntu 24.04), or give ITERATION" >&2; exit 1; }
	done
	(cd $(dir $(DEB)) && sha256sum --quiet -c $(notdir $(DEB)).sha256)
	built=$$(cat $(DEB).commit)
	[[ "$$built" != *-dirty ]] || { echo "[ERROR] $(DEB) was built with uncommitted changes" >&2; exit 1; }
	git merge-base --is-ancestor $$built HEAD || { echo "[ERROR] $(DEB) was built from $$built, which this branch does not contain" >&2; exit 1; }
	git diff --quiet $$built HEAD -- . ':(exclude)packaging/release-notes' \
	  || { echo "[ERROR] Commits since $(DEB) was built ($$built) change more than release notes: make release again" >&2; exit 1; }
	[[ -f "$(NOTES)" ]] || { echo "[ERROR] No release notes $(NOTES): write them, or give NOTES=file" >&2; exit 1; }
	glibc=$$(dpkg-deb -f $(DEB) Depends | sed -n 's/.*libc6 (>= \([0-9.]*\)).*/\1/p')
	dpkg --compare-versions "$${glibc:-0}" le $(OLDEST_GLIBC) \
	  || { echo "[ERROR] $(DEB) needs libc6 $$glibc, newer than Ubuntu 24.04's $(OLDEST_GLIBC): build it on Ubuntu 24.04" >&2; exit 1; }
	git branch -r --contains $$built | grep -q . || { echo "[ERROR] Push $$built to GitHub first" >&2; exit 1; }
	if git rev-parse -q --verify refs/tags/$(TAG) >/dev/null; then
	  [[ "$$(git rev-list -n 1 $(TAG))" == "$$built" ]] || { echo "[ERROR] Tag $(TAG) exists on another commit" >&2; exit 1; }
	else
	  git tag $(TAG) $$built
	fi
	git push origin $(TAG)
	gh release create $(TAG) -R $(REPO) --prerelease --verify-tag --title "PI.N.S. for Celestron Origin, test build origin$(ITERATION)" \
	  --notes-file $(NOTES) $(DEB) $(DEB).sha256

# The pinned simulator is fetched, or moved to the pin, first; the build then sees its sources as they are.
simulator:
	@if [[ "$(SIM_DIR)" == "$(SIM_PINNED)" ]]; then packaging/fetch-deps.sh sim; fi
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
