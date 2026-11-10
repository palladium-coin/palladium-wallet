#!/usr/bin/env bash
# Regtest end-to-end check of the CLI against a real Palladium node and plm-fulcrum, in Docker.
# Covers: sync, dust refusal and boundary (the node runs with -acceptnonstdtxn=0, so it enforces the
# relay policy), broadcast, coin freezing, taproot and WIF wallets, PSBT interoperability with the node, and the
# 2-of-3 multisig with the node as the reference for addresses, and the GUI view model against the real node
# (including a proxy that lies about broadcasts). Starts and removes its own containers and temp data
# (names plm-e2e-*, port 50111), so it does not touch a running mainnet or regtest setup.
#   usage: tests/regtest/e2e.sh        needs: docker, the .NET SDK (dotnet on PATH), python3
#   E2E_KEEP=1 keeps the containers and temp data after the run, to debug a failure
set -euo pipefail

NODE_IMAGE=${NODE_IMAGE:-ghcr.io/palladium-coin/palladiumcore:2.1.0}
FULCRUM_IMAGE=${FULCRUM_IMAGE:-ghcr.io/palladium-coin/plm-fulcrum:0.1.1}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
NET=plm-e2e
NODE=plm-e2e-node
FUL=plm-e2e-fulcrum
PORT=50111
CLI="docker exec $NODE palladium-cli -regtest -rpcwallet=t"
WALLET="$WORK/wallet.json"
fail=0

cleanup() {
  docker exec "$NODE" palladium-cli -regtest stop >/dev/null 2>&1 || true
  sleep 2
  docker rm -f "$FUL" "$NODE" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
[[ ${E2E_KEEP:-0} == 1 ]] || trap cleanup EXIT   # E2E_KEEP=1 leaves the containers up for debugging (remove them by hand)

check() { # check "description" expected-substring command...
  local what=$1 want=$2; shift 2
  local out; out=$("$@" 2>&1) || true
  if [[ $out == *"$want"* ]]; then echo "  ok    $what"
  else echo "  FAIL  $what (wanted '$want', got: ${out: -200})"; fail=1; fi
}

mkdir -p "$WORK/node" "$WORK/fulcrum"
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
docker run -d --name "$NODE" --network "$NET" -v "$WORK/node:/home/palladium/.palladium" -e HOME=/home/palladium \
  --user "$(id -u):$(id -g)" "$NODE_IMAGE" -regtest -acceptnonstdtxn=0 >/dev/null
for _ in $(seq 30); do docker exec "$NODE" palladium-cli -regtest getblockchaininfo >/dev/null 2>&1 && break; sleep 1; done
docker exec "$NODE" palladium-cli -regtest createwallet t >/dev/null
MINER=$($CLI getnewaddress "" bech32)
$CLI generatetoaddress 130 "$MINER" >/dev/null   # coinbase maturity is 120

docker run -d --name "$FUL" --network "$NET" -v "$WORK/fulcrum:/data" -e HOME=/data --user "$(id -u):$(id -g)" \
  -p 127.0.0.1:$PORT:50001 "$FULCRUM_IMAGE" plm-fulcrum /data/plm-fulcrum.conf >/dev/null
for _ in $(seq 60); do (echo > /dev/tcp/127.0.0.1/$PORT) 2>/dev/null && break; sleep 1; done
sleep 8 # Fulcrum indexes the 130 blocks

cd "$ROOT"
dotnet build src/Cli -v q >/dev/null
plm() { dotnet "$ROOT/src/Cli/bin/Debug/net10.0/PalladiumWallet.Cli.dll" "$@" --net regtest --file "$WALLET"; }
SRV=(--server "127.0.0.1:$PORT")

echo "== wallet and funding"
plm create --no-encryption >/dev/null 2>&1
ADDR=$(plm info | sed -n 's/^receive: *\([^ ]*\).*/\1/p')
$CLI sendtoaddress "$ADDR" 1 >/dev/null
$CLI generatetoaddress 2 "$MINER" >/dev/null
sleep 4
check "sync sees the funds" "Balance: 1.00000000 PLM spendable" plm sync "${SRV[@]}"

echo "== dust policy (nodes enforce 3 sat/vB)"
P2WPKH=$($CLI getnewaddress "" bech32); P2PKH=$($CLI getnewaddress "" legacy); P2SH=$($CLI getnewaddress "" p2sh-segwit)
check "293 sat to P2WPKH refused before signing" "dust limit (294 sat)" plm send --to "$P2WPKH" --amount 0.00000293 "${SRV[@]}"
check "545 sat to P2PKH refused before signing"  "dust limit (546 sat)" plm send --to "$P2PKH"  --amount 0.00000545 "${SRV[@]}"
check "539 sat to P2SH refused before signing"   "dust limit (540 sat)" plm send --to "$P2SH"   --amount 0.00000539 "${SRV[@]}"
check "294 sat to P2WPKH broadcast and accepted" "Broadcast:" plm send --to "$P2WPKH" --amount 0.00000294 --feerate 2 --broadcast "${SRV[@]}"
$CLI generatetoaddress 1 "$MINER" >/dev/null; sleep 3
check "546 sat to P2PKH broadcast and accepted"  "Broadcast:" plm send --to "$P2PKH" --amount 0.00000546 --feerate 2 --broadcast "${SRV[@]}"
$CLI generatetoaddress 1 "$MINER" >/dev/null; sleep 3

echo "== coin freezing"
plm sync "${SRV[@]}" >/dev/null
COIN=$(plm utxos | grep -o '[0-9a-f]\{64\}:[0-9]*' | head -1)
check "freeze a coin" "Frozen: $COIN" plm freeze "$COIN"
check "a frozen coin is not spent" "No spendable UTXOs" plm send --to "$P2WPKH" --amount 0.0001 "${SRV[@]}"
check "unfreeze the coin" "Unfrozen: $COIN" plm unfreeze "$COIN"
check "the coin can be spent again" "Signed transaction" plm send --to "$P2WPKH" --amount 0.0001 "${SRV[@]}"

echo "== taproot (BIP86) and imported WIF keys: fund, sync, spend, confirmed by the node"
other() { dotnet "$ROOT/src/Cli/bin/Debug/net10.0/PalladiumWallet.Cli.dll" "$@" --net regtest; }
fund_and_spend() { # fund_and_spend description wallet-file
  local what=$1 file=$2 addr
  addr=$(other info --file "$file" | sed -n 's/^receive: *\([^ ]*\).*/\1/p')
  $CLI sendtoaddress "$addr" 1 >/dev/null; $CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 4
  check "$what: sync sees the funds" "Balance: 1.00000000 PLM spendable" other sync "${SRV[@]}" --file "$file"
  check "$what: spend broadcast and accepted" "Broadcast:" \
    other send --to "$($CLI getnewaddress "" bech32)" --amount 0.1 --feerate 2 --broadcast "${SRV[@]}" --file "$file"
  $CLI generatetoaddress 1 "$MINER" >/dev/null; sleep 3
  check "$what: the spend is mined (mempool empty)" '"size": 0' docker exec "$NODE" palladium-cli -regtest getmempoolinfo
}
other create --kind taproot --no-encryption --file "$WORK/taproot.json" >/dev/null 2>&1
check "taproot address is bech32m (witness v1)" "receive:  rplm1p" other info --file "$WORK/taproot.json"
fund_and_spend "taproot" "$WORK/taproot.json"
$CLI dumpprivkey "$($CLI getnewaddress "" bech32)" | other restore-wif - --no-encryption --file "$WORK/wif.json" >/dev/null 2>&1
check "WIF wallet is spendable" "imported: 1 private key(s)" other info --file "$WORK/wif.json"
fund_and_spend "WIF" "$WORK/wif.json"

echo "== PSBT interoperability with Palladium Core as the external party"
# (A) our watch-only wallet builds the PSBT, the node's wallet (an independent implementation) signs it.
WO_ADDR=$($CLI getnewaddress "" bech32); $CLI sendtoaddress "$WO_ADDR" 1 >/dev/null
$CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 4
other restore-address "$WO_ADDR" --no-encryption --file "$WORK/watch.json" >/dev/null 2>&1
PSBT=$(other send --to "$($CLI getnewaddress "" bech32)" --amount 0.1 --feerate 2 "${SRV[@]}" --file "$WORK/watch.json" 2>&1 | tail -1)
check "our unsigned PSBT carries the previous transaction" "non_witness_utxo" $CLI decodepsbt "$PSBT"
SIGNED=$($CLI walletprocesspsbt "$PSBT")
check "the node signs our PSBT completely" '"complete": true' echo "$SIGNED"
HEX=$($CLI finalizepsbt "$(echo "$SIGNED" | python3 -c 'import sys,json;print(json.load(sys.stdin)["psbt"])')" \
      | python3 -c 'import sys,json;print(json.load(sys.stdin)["hex"])')
check "the node accepts the transaction it signed from our PSBT" '"allowed": true' $CLI testmempoolaccept "[\"$HEX\"]"
# (B) the node creates the PSBT (witness_utxo only, as a minimal external creator does), our wallet signs it.
other create --no-encryption --file "$WORK/signer.json" >/dev/null 2>&1
OUR_ADDR=$(other info --file "$WORK/signer.json" | sed -n 's/^receive: *\([^ ]*\).*/\1/p')
COIN_TX=$($CLI sendtoaddress "$OUR_ADDR" 1); $CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 3
COIN_VOUT=$($CLI gettransaction "$COIN_TX" | python3 -c "import sys,json;print([d['vout'] for d in json.load(sys.stdin)['details'] if d['address']=='$OUR_ADDR'][0])")
RAW=$($CLI createpsbt "[{\"txid\":\"$COIN_TX\",\"vout\":$COIN_VOUT}]" "[{\"$($CLI getnewaddress "" bech32)\":0.5},{\"$OUR_ADDR\":0.4999}]")
EXT_PSBT=$($CLI utxoupdatepsbt "$RAW")
OUT=$(other sign-psbt "$EXT_PSBT" --file "$WORK/signer.json" 2>&1 || true)
check "our wallet signs a PSBT made by the node" "signed" echo "$OUT"
check "and the node recognises the wallet's output as change" "(yours: change)" echo "$OUT"
FINAL=$(echo "$OUT" | grep -E '^0[0-9a-f]{100,}$' | tail -1)
check "the node accepts the transaction we signed from its PSBT" '"allowed": true' $CLI testmempoolaccept "[\"$FINAL\"]"

echo "== 2-of-3 multisig (P2WSH sortedmulti): addresses equal the node's, spend signed by two cosigners, broadcast"
W1="abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"
W2="zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong"
W3="legal winner thank year wave sausage worth useful legal winner thank yellow"
K1=$(other cosigner-key "$W1"); K2=$(other cosigner-key "$W2"); K3=$(other cosigner-key "$W3")
n=0; for words in "$W1" "$W2" "$W3"; do n=$((n+1))
  other create-multisig --threshold 2 --cosigner "$K1" --cosigner "$K2" --cosigner "$K3" --mnemonic "$words" \
    --no-encryption --file "$WORK/ms$n.json" >/dev/null 2>&1
done
DESCRIPTOR=$(other info --file "$WORK/ms1.json" | sed -n 's/^descriptor: //p')
NODE_DESCRIPTOR=$($CLI getdescriptorinfo "$DESCRIPTOR" | python3 -c 'import sys,json;print(json.load(sys.stdin)["descriptor"])')
MS_ADDR=$(other info --file "$WORK/ms1.json" | sed -n 's/^receive: *\([^ ]*\).*/\1/p')
check "the node derives the same multisig address from the descriptor" "$MS_ADDR" $CLI deriveaddresses "$NODE_DESCRIPTOR" "[0,0]"
check "the wallet ID is the checksum the node computes for the descriptor" \
  "$($CLI getdescriptorinfo "$DESCRIPTOR" | python3 -c 'import sys,json;print(json.load(sys.stdin)["checksum"])')" \
  other info --file "$WORK/ms1.json"
for n in 2 3; do
  check "cosigner $n derives the same address" "$MS_ADDR" other info --file "$WORK/ms$n.json"
done
$CLI sendtoaddress "$MS_ADDR" 1 >/dev/null; $CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 4
check "the multisig wallet sees the funds" "Balance: 1.00000000 PLM spendable" other sync "${SRV[@]}" --file "$WORK/ms1.json"
MS_OUT=$(other send --to "$($CLI getnewaddress "" bech32)" --amount 0.3 --feerate 2 "${SRV[@]}" --file "$WORK/ms1.json" 2>&1)
check "cosigner 1 builds a PSBT that carries its signature" "signed by this wallet" echo "$MS_OUT"
MS_PSBT=$(echo "$MS_OUT" | tail -1)
check "one of two signatures is not complete" "Incomplete" other sign-psbt "$MS_PSBT" --file "$WORK/ms1.json"
COPY3=$(other sign-psbt "$MS_PSBT" --file "$WORK/ms3.json" 2>&1 | grep -E '^cHNi' | head -1)
check "cosigners 1 and 3 combine their copies into a final transaction" "Final transaction:" other combine-psbt "$MS_PSBT" "$COPY3"
check "cosigner 2 completes it and the node takes it" "Broadcast:" other sign-psbt "$MS_PSBT" --broadcast "${SRV[@]}" --file "$WORK/ms2.json"
$CLI generatetoaddress 1 "$MINER" >/dev/null; sleep 3
check "the multisig spend is mined (mempool empty)" '"size": 0' docker exec "$NODE" palladium-cli -regtest getmempoolinfo

echo "== 2-of-3 wrapped multisig (P2SH-P2WSH): address and wallet ID equal the node's, spend signed by two cosigners, broadcast"
WK1=$(other cosigner-key "$W1" --wrapped); WK2=$(other cosigner-key "$W2" --wrapped); WK3=$(other cosigner-key "$W3" --wrapped)
n=0; for words in "$W1" "$W2" "$W3"; do n=$((n+1))
  other create-multisig --threshold 2 --wrapped --cosigner "$WK1" --cosigner "$WK2" --cosigner "$WK3" --mnemonic "$words" \
    --no-encryption --file "$WORK/wms$n.json" >/dev/null 2>&1
done
WDESC=$(other info --file "$WORK/wms1.json" | sed -n 's/^descriptor: //p')
WMS_ADDR=$(other info --file "$WORK/wms1.json" | sed -n 's/^receive: *\([^ ]*\).*/\1/p')
check "the descriptor is a sh(wsh(...))" "sh(wsh(sortedmulti(2," echo "$WDESC"
check "the node derives the same wrapped multisig address" "$WMS_ADDR" $CLI deriveaddresses "$($CLI getdescriptorinfo "$WDESC" | python3 -c 'import sys,json;print(json.load(sys.stdin)["descriptor"])')" "[0,0]"
check "the wallet ID is the checksum the node computes for the descriptor" \
  "$($CLI getdescriptorinfo "$WDESC" | python3 -c 'import sys,json;print(json.load(sys.stdin)["checksum"])')" \
  other info --file "$WORK/wms1.json"
$CLI sendtoaddress "$WMS_ADDR" 1 >/dev/null; $CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 4
check "the wrapped multisig wallet sees the funds" "Balance: 1.00000000 PLM spendable" other sync "${SRV[@]}" --file "$WORK/wms1.json"
WMS_OUT=$(other send --to "$($CLI getnewaddress "" bech32)" --amount 0.3 --feerate 2 "${SRV[@]}" --file "$WORK/wms1.json" 2>&1)
check "cosigner 1 builds a PSBT that carries its signature" "signed by this wallet" echo "$WMS_OUT"
WMS_PSBT=$(echo "$WMS_OUT" | tail -1)
check "cosigner 2 completes it and the node takes it (redeem script and witness)" "Broadcast:" other sign-psbt "$WMS_PSBT" --broadcast "${SRV[@]}" --file "$WORK/wms2.json"
$CLI generatetoaddress 1 "$MINER" >/dev/null; sleep 3
check "the wrapped multisig spend is mined (mempool empty)" '"size": 0' docker exec "$NODE" palladium-cli -regtest getmempoolinfo

echo "== GUI view model against the real node (Coins tab, freeze, send, donate, broadcast reconciliation through a lying proxy)"
GUI_ADDR=$(dotnet "$ROOT/src/Cli/bin/Debug/net10.0/PalladiumWallet.Cli.dll" addresses \
  "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about" --net regtest --count 1 \
  | sed -n 's/^ *receiving\/0: *//p')
for _ in 1 2 3 4; do $CLI sendtoaddress "$GUI_ADDR" 1 >/dev/null; done
$CLI generatetoaddress 2 "$MINER" >/dev/null; sleep 4
check "no transaction waits in the mempool before the GUI run" '"size": 0' docker exec "$NODE" palladium-cli -regtest getmempoolinfo
GUI_RESULT=$(PALLADIUM_TEST_REGTEST_SERVER="127.0.0.1:$PORT" PALLADIUM_TEST_REGTEST_DEST="$($CLI getnewaddress "" bech32)" \
  dotnet test "$ROOT/tests/PalladiumWallet.Tests" --filter "FullyQualifiedName~RegtestGuiTests" 2>&1 | tail -15 || true)
[[ $GUI_RESULT == *"Passed!"* ]] || echo "$GUI_RESULT"
check "the GUI tests pass" "Passed!" echo "$GUI_RESULT"
check "the GUI broadcast four transactions to the node" '"size": 4' docker exec "$NODE" palladium-cli -regtest getmempoolinfo

echo
if [[ $fail == 0 ]]; then echo "regtest e2e: all checks passed"; else echo "regtest e2e: FAILED"; exit 1; fi
