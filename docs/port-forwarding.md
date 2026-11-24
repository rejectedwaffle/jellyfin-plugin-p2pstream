# Port forwarding through a VPN

Through a VPN, peers can only connect *to* you on a port the VPN forwards. Without one, P2P Stream only makes outgoing
connections. That works, but many peers can't be reached that way, so torrents with few seeders start and play slower.

Most VPNs that offer port forwarding hand out a **random** port, which can change when the tunnel reconnects. So the
plugin doesn't ask for a fixed port here. It reads the current one from a file.

## The setting

**Network** tab > **VPN** box > **Forwarded port file**: the path of a file that holds the forwarded port number,
for example `/run/pia-port`.

- The plugin reads it at startup and every 15 seconds, and listens on that port (for peers and DHT) instead of the
  **Listen port**. The box below the field shows which port is in use, or why the file couldn't be used.
- The file only needs to contain the number (`41234`). Anything around it is ignored (`port=41234` works too).
- The Jellyfin user must be able to read it. A world-readable file in `/run` is fine.
- If the file disappears or holds no number, the plugin falls back to the normal listen port.

The **Test** button on the Network tab includes a **Port forwarding** row. It connects to your VPN address on that port from
outside the VPN, so **Open** means peers can really reach you.

## Keeping the file current

Something on the Jellyfin machine (or container) has to ask the VPN for the port, keep it alive, and write it to the
file. That's specific to each VPN, so it's a small script rather than part of the plugin.

- **gluetun:** it writes the forwarded port to a file itself (`/tmp/gluetun/forwarded_port` by default, see
  `VPN_PORT_FORWARDING_STATUS_FILE`). If Jellyfin shares gluetun's network, mount that file into the Jellyfin container
  and point the setting at it.
- **Private Internet Access (WireGuard):** a complete example follows.
- **Other VPNs** (ProtonVPN's NAT-PMP, AirVPN's fixed ports, ...): any script that writes the port number to a file
  works. If the port never changes, a file with the number in it is enough, or just set the **Listen port**.

## Example: Private Internet Access (WireGuard, Linux)

This is the setup used while developing the plugin: Jellyfin in a Debian LXC container, PIA's official
[manual-connections](https://github.com/pia-foss/manual-connections) scripts cloned to `/opt/pia-manual`, and the
tunnel as a WireGuard interface named `pia`. It works for a full tunnel and a split tunnel alike, because every request
to PIA's port-forwarding service is sent through the `pia` interface.

PIA forwards ports on most regions, but not on US ones.

### 1. Your PIA login

Put your PIA username and password in a file only root can read:

```bash
cat > /root/.pia-creds <<'EOF'
PIA_USER=p1234567
PIA_PASS=your-password
EOF
chmod 600 /root/.pia-creds
```

### 2. The tunnel

If you don't have one yet, create the WireGuard config on a server that supports port forwarding (`PIA_PF=true` picks one).
Run as root, from `/opt/pia-manual`:

```bash
set -a; source /root/.pia-creds; set +a
VPN_PROTOCOL=wireguard PIA_PF=true DISABLE_IPV6=no DIP_TOKEN=no PIA_DNS=false PIA_CONNECT=false \
  PIA_CONF_PATH=/etc/wireguard/pia.conf AUTOCONNECT=true ./run_setup.sh
systemctl enable --now wg-quick@pia
```

`DISABLE_IPV6=no` matters: `yes` turns IPv6 off for the whole machine.

**Split tunnel (optional):** to send only torrent traffic through PIA and leave Jellyfin itself on your normal connection,
save this as `/root/pia-split.sh` and run it once (and again whenever the config is regenerated; step 4 does that).
Then set **Privacy method** to *Only through a VPN* with the `pia` interface.

```bash
#!/bin/bash
# Makes /etc/wireguard/pia.conf split-tunnel (only traffic from the VPN's own address uses it). Safe to run twice.
CONF=/etc/wireguard/pia.conf
grep -q '^Table = off' "$CONF" && exit 0
IP=$(sed -n 's/^Address = //p' "$CONF" | cut -d/ -f1)
sed -i "/^\[Interface\]/a Table = off\nPostUp = ip route add default dev %i table 51820; ip rule add from $IP table 51820 priority 100\nPreDown = ip rule del from $IP table 51820 priority 100; ip route del default dev %i table 51820" "$CONF"
```

### 3. Port forwarding: `/root/pia-pf.sh`

Asks PIA for a port, renews it every 15 minutes, and writes it to `/run/pia-port`.

```bash
#!/bin/bash
# Keeps a PIA forwarded port for the 'pia' tunnel and writes it to /run/pia-port (read by P2P Stream).
# Like PIA's port_forwarding.sh, but its requests go through the tunnel, which a split tunnel needs.
set -uo pipefail
cd /opt/pia-manual
set -a; source /root/.pia-creds; set +a
PORT_FILE=/run/pia-port
log(){ logger -t pia-pf "$*"; echo "$*"; }

until ip link show pia >/dev/null 2>&1; do sleep 2; done
for i in $(seq 1 30); do
  hs=$(wg show pia latest-handshakes | awk '{print $2}' | head -1)
  [ "${hs:-0}" -gt 0 ] && break; sleep 2
done
[ "${hs:-0}" -gt 0 ] || { log "no WireGuard handshake"; exit 1; }

# The server's hostname (needed for its TLS certificate), from PIA's server list.
EP=$(wg show pia endpoints | awk '{print $2}' | cut -d: -f1)
HOST=""
for i in 1 2 3 4 5; do
  HOST=$(curl -s --max-time 15 https://serverlist.piaservers.net/vpninfo/servers/v6 | head -1 \
    | jq -r --arg ip "$EP" '[.regions[] | select(.port_forward) | .servers.wg[]? | select(.ip==$ip)][0].cn // empty' 2>/dev/null) || true
  [ -n "$HOST" ] && break; sleep 3
done
[ -n "$HOST" ] || { log "server $EP doesn't offer port forwarding (run /root/pia-reconnect.sh to move to one that does)"; sleep 300; exit 1; }

timeout --foreground 30 ./get_token.sh </dev/null >/dev/null || { log "get_token failed"; exit 1; }
TOKEN=$(head -1 /opt/piavpn-manual/token)
[ -n "$TOKEN" ] || { log "empty token"; exit 1; }

pf(){ curl -s --max-time 15 --interface pia --connect-to "$HOST::$EP:" --cacert ca.rsa.4096.crt "$@"; }
SIG=$(pf -G --data-urlencode "token=$TOKEN" "https://$HOST:19999/getSignature")
[ "$(jq -r .status <<<"$SIG" 2>/dev/null)" = OK ] || { log "getSignature failed: $SIG"; exit 1; }
PAYLOAD=$(jq -r .payload <<<"$SIG"); SIGNATURE=$(jq -r .signature <<<"$SIG")
PORT=$(base64 -d <<<"$PAYLOAD" | jq -r .port)
log "forwarded port $PORT from $HOST, expires $(base64 -d <<<"$PAYLOAD" | jq -r .expires_at)"

while true; do
  R=$(pf -G --data-urlencode "payload=$PAYLOAD" --data-urlencode "signature=$SIGNATURE" "https://$HOST:19999/bindPort")
  [ "$(jq -r .status <<<"$R" 2>/dev/null)" = OK ] || { log "bindPort failed: $R"; rm -f "$PORT_FILE"; exit 1; }
  echo "$PORT" > "$PORT_FILE.tmp" && chmod 644 "$PORT_FILE.tmp" && mv "$PORT_FILE.tmp" "$PORT_FILE"
  sleep 900
done
```

Note `.servers.wg[]?` in the server-list lookup: some regions list no WireGuard servers, and without the `?` the lookup
fails for every server.

And its service, `/etc/systemd/system/pia-pf.service`:

```ini
[Unit]
Description=PIA port forwarding for P2P Stream (writes the port to /run/pia-port)
After=network-online.target wg-quick@pia.service
Requires=wg-quick@pia.service

[Service]
Type=simple
ExecStart=/root/pia-pf.sh
ExecStopPost=/bin/rm -f /run/pia-port
Restart=always
RestartSec=30

[Install]
WantedBy=multi-user.target
```

### 4. Optional: reconnecting when PIA drops the tunnel

PIA sometimes expires a WireGuard key, and then the tunnel stops passing traffic. A watchdog checks every 2 minutes. It
restarts the tunnel first, and if that doesn't help, registers a new key on a port-forwarding server (keeping the split
tunnel).

`/root/pia-reconnect.sh`:

```bash
#!/bin/bash
# Registers a new WireGuard key with PIA (on a port-forwarding server), keeps the split tunnel, restarts everything.
set -a; source /root/.pia-creds; set +a
cd /opt/pia-manual
LOGF=/var/log/pia-reconnect.log
log(){ logger -t pia-reconnect "$*"; echo "$*"; }
run_setup(){
  VPN_PROTOCOL=wireguard PIA_PF=true DISABLE_IPV6=no DIP_TOKEN=no PIA_DNS=false PIA_CONNECT=false \
  PIA_CONF_PATH=/etc/wireguard/pia.conf timeout --foreground 120 ./run_setup.sh </dev/null >>"$LOGF" 2>&1
}
touch "$LOGF"; chmod 600 "$LOGF"
systemctl stop pia-pf
systemctl stop wg-quick@pia 2>/dev/null || true
wg-quick down pia 2>/dev/null || true
sleep 2
REG="${PREFERRED_REGION:-aus}"   # your nearest region with port forwarding
if AUTOCONNECT=false PREFERRED_REGION="$REG" run_setup; then
  log "registered in region $REG"
elif ( unset PREFERRED_REGION; AUTOCONNECT=true run_setup ); then
  log "region $REG failed; registered via auto-selected PF region"
else
  log "registration FAILED"
  systemctl start wg-quick@pia 2>/dev/null || true
  systemctl start pia-pf 2>/dev/null || true
  exit 1
fi
[ -x /root/pia-split.sh ] && /root/pia-split.sh
systemctl start wg-quick@pia
sleep 5
systemctl start pia-pf
log "tunnel and port forwarding restarted"
```

`/root/pia-watchdog.sh`:

```bash
#!/bin/bash
exec 9>/run/pia-watchdog.lock; flock -n 9 || exit 0
FAILS=/run/pia-watchdog.fails; LAST=/run/pia-watchdog.lastreg; BACKOFF=/run/pia-watchdog.backoff; MAX_AGE=240
log(){ logger -t pia-watchdog "$*"; echo "$*"; }
healthy(){
  ip link show pia >/dev/null 2>&1 || { reason="interface missing"; return 1; }
  hs=$(wg show pia latest-handshakes | awk '{print $2}' | head -1); now=$(date +%s)
  if [ -z "$hs" ] || [ "$hs" -eq 0 ] || [ $((now-hs)) -gt $MAX_AGE ]; then reason="stale handshake"; return 1; fi
  # test through the tunnel itself (with a split tunnel, a plain ping would use the normal connection)
  curl -s --max-time 8 --interface pia -o /dev/null https://1.1.1.1/cdn-cgi/trace \
    || curl -s --max-time 8 --interface pia -o /dev/null https://9.9.9.9 \
    || { reason="no traffic through tunnel"; return 1; }
}
if healthy; then
  rm -f "$FAILS" "$BACKOFF"
  case "$(systemctl is-active pia-pf)" in inactive|failed) log "pia-pf was down while tunnel healthy; starting it"; systemctl start pia-pf ;; esac
  exit 0
fi
n=$(( $(cat "$FAILS" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$FAILS"
log "unhealthy ($reason), consecutive failures: $n"
[ "$n" -lt 2 ] && exit 0
if [ "$n" -eq 2 ]; then
  log "restarting wg-quick@pia"; systemctl restart wg-quick@pia; sleep 15
  if healthy; then log "recovered after tunnel restart"; systemctl restart pia-pf; rm -f "$FAILS"; fi
  exit 0
fi
cool=$(cat "$BACKOFF" 2>/dev/null || echo 600)
if [ -f "$LAST" ] && [ $(( $(date +%s) - $(stat -c %Y "$LAST") )) -lt "$cool" ]; then exit 0; fi
touch "$LAST"
log "re-registering with PIA (next retry in at least ${cool}s)"
if /root/pia-reconnect.sh; then sleep 10; if healthy; then log "recovered after re-registration"; rm -f "$FAILS" "$BACKOFF"; exit 0; fi; fi
echo $(( cool * 2 > 3600 ? 3600 : cool * 2 )) > "$BACKOFF"
log "re-registration did not restore the tunnel"
```

Its service and timer:

```ini
# /etc/systemd/system/pia-watchdog.service
[Unit]
Description=PIA WireGuard watchdog
After=network-online.target

[Service]
Type=oneshot
ExecStart=/root/pia-watchdog.sh
```

```ini
# /etc/systemd/system/pia-watchdog.timer
[Unit]
Description=Run PIA WireGuard watchdog every 2 minutes

[Timer]
OnBootSec=3min
OnUnitActiveSec=2min
AccuracySec=15s

[Install]
WantedBy=timers.target
```

### 5. Turn it on

```bash
chmod 700 /root/pia-*.sh
systemctl daemon-reload
systemctl enable --now pia-pf pia-watchdog.timer
sleep 20; journalctl -t pia-pf -n 5 --no-pager; cat /run/pia-port
```

You should see `forwarded port NNNNN` and the number. Then set **Forwarded port file** to `/run/pia-port` in the
plugin and press **Test**: the **Port forwarding** row should say **Open**.

**Troubleshooting**

- *"doesn't offer port forwarding"*: the tunnel is on a server without it. Run `/root/pia-reconnect.sh` once.
- *A dependency job for pia-pf.service failed*: the tunnel was started by hand (`wg-quick up pia`), so systemd can't
  start it. Run `wg-quick down pia; systemctl start wg-quick@pia pia-pf`.
- **Test** says *Closed*: check that `/run/pia-port` exists and holds the port the plugin shows in the VPN box.
