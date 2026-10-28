#!/usr/bin/env bash
set -Eeuo pipefail

cd /opt/trades
export IMAGE_TAG="${1:?Pass the image tag}"
[[ "$IMAGE_TAG" =~ ^sha-[a-f0-9]{40}$ ]] || { echo 'Invalid image tag'; exit 1; }

# The publishing secret travels over encrypted stdin, not shell arguments or .env.
IFS= read -r PUBLIC_JOURNAL_TOKEN
PUBLIC_JOURNAL_TOKEN="${PUBLIC_JOURNAL_TOKEN%$'\r'}"
[[ ${#PUBLIC_JOURNAL_TOKEN} -ge 32 && ${#PUBLIC_JOURNAL_TOKEN} -le 4096 && "$PUBLIC_JOURNAL_TOKEN" != *[!\!-~]* ]] || {
  echo 'Publishing token must contain 32–4096 printable ASCII characters without spaces'; exit 1;
}
export PUBLIC_JOURNAL_TOKEN

exec 9>/opt/trades/.deploy.lock
flock -w 600 9
archive="public-journal-$IMAGE_TAG.tar.gz"
trap 'rm -f "$archive"' EXIT

# Match the existing public-journal project/volume identity. Never delete its data.
sudo -n docker network inspect int >/dev/null
sudo -n --preserve-env=IMAGE_TAG,PUBLIC_JOURNAL_TOKEN docker compose -f compose.production.yml config --quiet
sudo -n docker load --input "$archive"
sudo -n install -m 644 nginx-vhost.conf /home/ubuntu/docker/nginx/letsencrypt-vhost-d/trades.shaa.one
sudo -n docker exec nginx-proxy nginx -t
sudo -n --preserve-env=IMAGE_TAG,PUBLIC_JOURNAL_TOKEN docker compose -f compose.production.yml up -d --wait --wait-timeout 180 journal
sudo -n docker exec nginx-proxy nginx -s reload
printf '%s\n' "$IMAGE_TAG" > current-image-tag
