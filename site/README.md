# Stage Manager Lai website

The public website is deployed from `site/` to GitHub Pages at
`https://franklai.com/StageManager/`. The repository's `github.io` URL redirects
to this account-level custom domain. It is a browser
simulation; it does not read or control visitors' windows. The app remains a
modified fork of Andreas Wäscher's [StageManager](https://github.com/awaescher/StageManager).

Run locally from the repository root:

```powershell
python -m http.server 8765 --directory site
```

Open `http://127.0.0.1:8765/`. `site/data/release.json` is a checked-in
development baseline. Every Pages deployment runs `build_release.py` against
GitHub's latest stable Release API. The builder verifies the EXE filename,
source URL, size and SHA-256 digest before assembling a deploy directory. If
these checks fail, deployment stops and the existing website remains online.

The demo offers three one-click scenarios (groups, Focus, and full screen),
independently pinned groups, keyboard navigation, and a reversible minimize-all
action. Chinese and English use the same demo state. Cards are mounted once and
updated in place so selecting a window does not restart expansion animations.
The site uses local assets and system fonts, with no analytics or external UI libraries.

The download area can copy the verified EXE link and SHA-256. The builder also
embeds working download links, version text, and SoftwareApplication metadata in
HTML, so downloads still work without JavaScript. Preview imagery is illustrative.
Published CSS and JavaScript links carry content fingerprints to avoid stale
assets after an update.

The site publishes on changes to `site/` on `main`, a GitHub Release being
published, or a manual workflow dispatch. After adding the workflow, configure
the repository Pages source as **GitHub Actions**.
