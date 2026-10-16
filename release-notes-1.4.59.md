Improve character backup rotation and reduce transfer overhead.

- Keep two independent timestamped ZIP backups, with a minimum 30-minute interval and a newer source modification timestamp required.
- Verify and flush each new backup before publication; remove older backups afterward. Preserve legacy backups until two new-format copies exist.
- Use Fastest compression for transfers and avoid unnecessary buffer copies.
- Remove the unused profile cache and startup profile preloading.
- Document backup rotation and preserve the requirements edited on GitHub.

Install FuriousServerCharacters.dll version 1.4.59 on both server and clients.
