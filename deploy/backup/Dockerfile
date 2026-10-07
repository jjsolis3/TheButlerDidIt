# The nightly database backup (docker-compose.yml's `backup` service): the backup image, plus a check after each
# backup that it can actually be restored (#103). The image runs every file in /hooks after a backup; run-parts
# skips names with dots, hence no ".sh".
FROM prodrigestivill/postgres-backup-local:16
COPY check-restore /hooks/50-check-restore
RUN chmod 755 /hooks/50-check-restore
