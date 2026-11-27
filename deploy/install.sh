#!/bin/sh
# Run in the dedicated Debian guest as root after installing Python and the GitHub CLI.
set -eu
cd "$(dirname "$0")"
test -r /etc/live/webhook.secret
test -r /etc/live/status.env
getent group live-observe >/dev/null || groupadd --system live-observe
getent group live-signal >/dev/null || groupadd --system live-signal
id live >/dev/null 2>&1 || useradd --system --user-group --home-dir /var/lib/live --shell /usr/sbin/nologin live
id live-web >/dev/null 2>&1 || useradd --system --user-group --no-create-home --shell /usr/sbin/nologin live-web
install -d -m 0750 -o root -g live /etc/live
install -d -m 0755 /opt/live/deploy /opt/live/host
install -d -m 0750 -o live -g live /var/lib/live
install -d -m 2750 -o live -g live-observe /var/lib/live-observation
install -m 0644 release_lib.py status_api.py live_deploy.py /opt/live/deploy/
install -m 0644 ../configs/world-only.json /opt/live/deploy/
install -m 0644 systemd/live-*.service systemd/live-*.timer systemd/live-*.path /etc/systemd/system/
install -m 0644 systemd/live-inbox.conf /etc/tmpfiles.d/
if ! test -e /var/lib/live/.reconcile.lock; then
    install -m 0640 -o live -g live /dev/null /var/lib/live/.reconcile.lock
fi
systemd-tmpfiles --create /etc/tmpfiles.d/live-inbox.conf
systemctl daemon-reload
if ! test -e /var/lib/live/world/recovery.json; then
    python3 /opt/live/deploy/live_deploy.py bootstrap "$@"
fi
test -x /opt/live/host/current/ArtificialLife.Cli
chown root:live /etc/live/world.json
chmod 0640 /etc/live/world.json
systemctl enable --now live-world.service live-status.service live-reconcile.path live-reconcile.timer
