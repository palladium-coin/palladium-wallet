#!/usr/bin/env bash
# Stops and removes what tests/regtest/up.sh started (containers, network, data).
docker exec plm-gui-regtest-node palladium-cli -regtest stop >/dev/null 2>&1 || true
sleep 2
docker rm -f plm-gui-regtest-fulcrum plm-gui-regtest-node >/dev/null 2>&1 || true
docker network rm plm-gui-regtest >/dev/null 2>&1 || true
rm -rf "${PLM_REGTEST_DIR:-/tmp/plm-gui-regtest}" 2>/dev/null || true
echo "regtest stopped"
