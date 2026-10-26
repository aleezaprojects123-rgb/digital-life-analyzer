# Digital Life Analyzer (DLA)

Digital Life Analyzer is an AI time coach for Windows. A browser extension (Chrome and Edge) and a Windows desktop agent (a C# / .NET 10 tray app) record which app or website is active and for how long, on the user's own PC. An on-device AI model labels that time (Study, Work, Entertainment, Social Media or Other). The raw records stay on the PC. Only encrypted summaries are sent to the cloud, where a web dashboard shows reports and offers a distraction forecast, a nudge and a recovery plan. Typing and screenshots are never recorded.

## Folder layout

```
digital-life-analyzer/
  device/              Aleeza's side (runs on the user's PC)
    agent/             Windows desktop agent (C# / .NET 10 tray app)
    extension/         Chrome and Edge browser extension (Manifest V3)
  cloud/               Fatima's side (backend, database, dashboard)
  contracts/           Shared agreements between the two sides
  .env.example         Template of environment variable names (no real values)
  .gitignore           Files git must never commit
  README.md            This file
```

## Who owns what

| Folder | Owner |
|---|---|
| `device/` | Aleeza |
| `cloud/` | Fatima |
| `contracts/` | Shared: both must approve any change |

## Working rules

1. Aleeza and Fatima commit directly to main, only inside their own folder (device/ for Aleeza, cloud/ for Fatima), and run git pull before every push. Never force-push. Changes to contracts/ go through a pull request and need the other person's approval. Proposed by Aleeza on 27 Oct 2026; Fatima to confirm.
2. Only edit your own folder.
3. Changes in `contracts/` need approval from both of us.

## PUBLIC REPO RULES

**This repository is public. Anyone on the internet can read everything in it, including old commits.**

- **Never commit** passwords, API keys (Stripe, database), encryption keys, `.env` files, real activity data, or screenshots of real browsing.
- Use **test data only** in examples, tests and screenshots.
- Real secrets go in a local `.env` file (copy `.env.example`), which is ignored by git.
- If a secret is committed by mistake, tell the other person straight away. Deleting it in a later commit is not enough, because it stays in the history: the secret must be replaced (rotated) at its source.

## License

The license is still to be decided. Until a license file is added, no license is granted. Do not copy this code into other projects without asking the owners.
