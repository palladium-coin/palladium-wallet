#!/usr/bin/env bash
# Starts a regtest Palladium Core node + plm-fulcrum for trying the GUI by hand; stop it with tests/regtest/down.sh.
# Throw-away by design: a fresh chain every time (data in /tmp/plm-gui-regtest, wiped by down.sh).
# Containers plm-gui-regtest-node and plm-gui-regtest-fulcrum, port 50111; separate from the e2e.sh ones (plm-e2e-*).
#   usage: tests/regtest/up.sh        then: dotnet run --project src/App.Desktop -- --net regtest
set -euo pipefail
NODE_IMAGE=${NODE_IMAGE:-ghcr.io/palladium-coin/palladiumcore:2.1.0}
FULCRUM_IMAGE=${FULCRUM_IMAGE:-ghcr.io/palladium-coin/plm-fulcrum:0.1.1}
NET=plm-gui-regtest NODE=plm-gui-regtest-node FUL=plm-gui-regtest-fulcrum PORT=50111
WORK=${PLM_REGTEST_DIR:-/tmp/plm-gui-regtest}
CLI="docker exec $NODE palladium-cli -regtest -rpcwallet=t"

if docker inspect "$NODE" >/dev/null 2>&1; then echo "already running: tests/regtest/down.sh first"; exit 1; fi
rm -rf "$WORK"; mkdir -p "$WORK/node" "$WORK/fulcrum"
cat >"$WORK/node/palladium.conf" <<CONF
server=1
txindex=1
fallbackfee=0.0001
[regtest]
rpcuser=u
rpcpassword=p
rpcport=18332
rpcbind=0.0.0.0
rpcallowip=172.16.0.0/12
zmqpubhashblock=tcp://0.0.0.0:28332
CONF
cat >"$WORK/fulcrum/plm-fulcrum.conf" <<CONF
palladiumd = $NODE:18332
rpcuser = u
rpcpassword = p
tcp = 0.0.0.0:50001
zmq = tcp://$NODE:28332
CONF
chmod 777 "$WORK/node" "$WORK/fulcrum"; chmod 666 "$WORK"/node/palladium.conf "$WORK"/fulcrum/plm-fulcrum.conf

docker network create "$NET" >/dev/null
docker run -d --name "$NODE" --network "$NET" -v "$WORK/node:/node/.palladium" -e HOME=/node \
  --user "$(id -u):$(id -g)" "$NODE_IMAGE" -regtest -acceptnonstdtxn=0 >/dev/null
up=0
for _ in $(seq 30); do docker exec "$NODE" palladium-cli -regtest getblockchaininfo >/dev/null 2>&1 && { up=1; break; }; sleep 1; done
[[ $up == 1 ]] || { echo "node did not come up:"; docker logs "$NODE" 2>&1 | tail -40; exit 1; }
docker exec "$NODE" palladium-cli -regtest createwallet t >/dev/null
MINER=$($CLI getnewaddress "" bech32)
$CLI generatetoaddress 130 "$MINER" >/dev/null   # coinbase maturity is 120
docker run -d --name "$FUL" --network "$NET" -v "$WORK/fulcrum:/data" -e HOME=/data --user "$(id -u):$(id -g)" \
  -p 127.0.0.1:$PORT:50001 "$FULCRUM_IMAGE" plm-fulcrum /data/plm-fulcrum.conf >/dev/null
for _ in $(seq 60); do (echo > /dev/tcp/127.0.0.1/$PORT) 2>/dev/null && break; sleep 1; done
sleep 8 # Fulcrum indexes the 130 blocks

cat <<MSG
regtest is up (130 blocks mined, spendable balance on the node's wallet "t").
  GUI server : 127.0.0.1:$PORT  (no SSL)
  launch GUI : dotnet run --project src/App.Desktop -- --net regtest
  fund a GUI address : docker exec $NODE palladium-cli -regtest -rpcwallet=t sendtoaddress <address> 1
  confirm it         : docker exec $NODE palladium-cli -regtest -rpcwallet=t generatetoaddress 1 $MINER
  stop and wipe      : tests/regtest/down.sh
MSG
