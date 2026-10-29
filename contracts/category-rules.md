# Contract 3: Category rules (version 1)

Status: **draft written by Fatima, needs Aleeza's approval.** This is a shared file: any change to the format needs approval from both Aleeza and Fatima.
The data is [category-rules.json](category-rules.json). The machine-readable format is [category-rules.schema.json](category-rules.schema.json). If this file and the schema disagree, fix them together.
Anything marked **[PROPOSAL]** is Fatima's suggestion and is not agreed until Aleeza approves it.
The test set and the measuring script are in [cloud/category-rules-testset/](../cloud/category-rules-testset/README.md).

## Purpose

The rules give a category (`Study`, `Work`, `Entertainment`, `Social Media` or `Other`) to an **app** or a **website** from its name alone. They are:

- the **fallback** when the on-device model is off or not available (F6): the agent labels with `label_source: "rules"`;
- the **baseline** the model must beat on Day 7.

The rules read **names only**: the process name of an app, the host of a website. They never read titles, page text or anything the user typed. The file holds no personal data.

## The file

```json
{
  "schema_version": "1",
  "rules_version": 1,
  "updated": "2026-10-29",
  "default_category": "Other",
  "apps":  [ {"process": "code", "category": "Work"}, ... ],
  "sites": [ {"match": "suffix", "value": "docs.google.com", "category": "Work"}, ... ]
}
```

| Field | Meaning |
|---|---|
| `schema_version` | Version of this **format**. Always `"1"`. |
| `rules_version` | Version of the **content**. Starts at 1 and goes up by one every time any rule is added, removed, moved or changed. |
| `updated` | The day the content last changed (`YYYY-MM-DD`). |
| `default_category` | What anything unmatched gets. Always `"Other"`. |
| `apps` | App rules, in order. |
| `sites` | Website rules, in order. |

An **app rule** is `{ "process", "category" }`. `process` is the process name in **lowercase, without `.exe`, with no spaces and no path** (`Code.exe` is written `code`). It matches the **whole** name.

A **site rule** is `{ "match", "value", "category" }`:

- `"match": "exact"`: the site is exactly `value` (`google.com` matches `google.com`, not `docs.google.com`). An exact value has a dot.
- `"match": "suffix"`: the site is `value`, **or ends with a dot and `value`**. So `google.com` matches `google.com` and `docs.google.com`, but **not** `notgoogle.com`. A suffix may be a single word, for example `edu` (every site ending in `.edu`).

Both kinds may carry a `note` (text for people, never used for matching).

## How a rule is applied

For one event:

1. **An extension event** (it has a site) uses the `sites` list. **An agent event** (it has only an app) uses the `apps` list. If both are given, the site wins. If neither is given, the answer is the default.
2. **Tidy the name first.**
   - App: remove spaces around it, make it lowercase, and remove one trailing `.exe`. So `Code.exe`, `CODE.EXE` and `code` all become `code`.
   - Site: remove spaces around it, make it lowercase, remove a trailing dot, and remove **one** leading `www.`.
3. **Go down the list from the top. The first rule that matches decides.** Nothing after it is looked at.
4. **If no rule matches, the category is `Other`.**

### The rule order

**The order is part of the meaning.** More specific rules must come before more general ones. For example `docs.google.com` (Work) must come before any rule for `google.com`; if it came after, it would never be reached.

The shipped file uses this order for `sites`:

1. specific hosts that differ from their parent site (`docs.google.com`, `scholar.google.com`, `learn.microsoft.com`...);
2. schools and universities (`edu`, `edu.pk`, `ac.uk`...) and government sites (`gov`, `gov.pk`...);
3. whole sites (`github.com`, `youtube.com`...);
4. exact rules (`google.com` exact = the bare search site).

A rule that an earlier rule always catches is a mistake. The tests find it, and so does no process listed twice. **Whoever edits `category-rules.json` must run the tests** (see the test set README).

### Which site name to pass

The extension's `domain` is the **registrable domain**: `docs.google.com` is stored as `google.com`. So rules for `docs.google.com` or `mail.google.com` can only match if the agent passes **the host of the page** (taken from the sanitized `url`, which is on the PC) and not only `domain`. **[PROPOSAL]** Pass the host when the event has a `url`; otherwise pass `domain`. If the agent passes only the registrable domain, the rules still work, but the host-specific ones fall back to the broader rule (measured in the test set README: 16 of 288 examples lose their answer).

## What the agent does with the answer **[PROPOSAL]**

When the answer comes from the rules, the event gets (Contract 1):

- `category`: the answer;
- `label_source`: `"rules"`;
- `confidence`: `null` (rules have no confidence);
- `uncertain`: `false` (but see open question 4).

Whether the model overrides the rules when it is on and sure is the agent's decision (F6), not part of this contract.

## Loader example for the C# agent

This is a complete, small class. It does exactly what "How a rule is applied" says. It was compiled and run against the whole test set, and gives the same answer as the Python reference ([category_rules.py](../cloud/category-rules-testset/category_rules.py)) for every example.

```csharp
using System.Text.Json;

/// Applies contracts/category-rules.json. Same behaviour as cloud/category-rules-testset/category_rules.py.
public sealed class CategoryRules
{
    private sealed record AppRule(string Process, string Category);
    private sealed record SiteRule(bool Exact, string Value, string Category);

    private readonly List<AppRule> _apps = new();
    private readonly List<SiteRule> _sites = new();
    private readonly string _default;

    public int RulesVersion { get; }

    public CategoryRules(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("schema_version").GetString() != "1")
            throw new InvalidDataException("Unsupported category rules format.");
        RulesVersion = root.GetProperty("rules_version").GetInt32();
        _default = root.GetProperty("default_category").GetString()!;
        foreach (var r in root.GetProperty("apps").EnumerateArray())
            _apps.Add(new AppRule(r.GetProperty("process").GetString()!, r.GetProperty("category").GetString()!));
        foreach (var r in root.GetProperty("sites").EnumerateArray())
            _sites.Add(new SiteRule(r.GetProperty("match").GetString() == "exact",
                                    r.GetProperty("value").GetString()!, r.GetProperty("category").GetString()!));
    }

    /// An extension event passes `site`; an agent event passes `app`. Nothing, or nothing matched, gives "Other".
    public string Categorize(string? app, string? site)
    {
        if (!string.IsNullOrWhiteSpace(site))
        {
            var host = site.Trim().ToLowerInvariant().TrimEnd('.');
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            foreach (var r in _sites)
                if (host == r.Value || (!r.Exact && host.EndsWith("." + r.Value, StringComparison.Ordinal)))
                    return r.Category;
        }
        else if (!string.IsNullOrWhiteSpace(app))
        {
            var name = app.Trim().ToLowerInvariant();
            if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
            foreach (var r in _apps)
                if (name == r.Process) return r.Category;
        }
        return _default;
    }
}
```

Using it:

```csharp
var rules = new CategoryRules(File.ReadAllText(rulesPath));   // load once, when the agent starts

rules.Categorize(app: "Code.exe", site: null);                 // "Work"      (agent event)
rules.Categorize(app: null, site: "docs.google.com");          // "Work"      (extension event)
rules.Categorize(app: null, site: "WWW.Example-Unknown.com");  // "Other"     (no rule)
```

Notes for the agent:

- Load the file **once** at start, not per event. About 320 rules are scanned from the top for each event, which is fast enough; a dictionary is possible later for the exact app names.
- A missing, unreadable or wrong-format file must not stop tracking: label everything `Other` and log one line.
- To check your own implementation, run it over [testset.json](../cloud/category-rules-testset/testset.json) and compare with `python cloud/category-rules-testset/evaluate.py --json`: the answers must be identical.

## Who changes what **[PROPOSAL]**

- **Format changes** (this file, the schema, the field names): both approve, as for every file in `contracts/`.
- **Content changes** (adding, moving or fixing a rule and raising `rules_version`): Fatima, in a pull request with the tests passing; Aleeza is told. Adding a rule never needs a code change in the agent.

## How good are the rules?

Short answer: **they are accurate when they have a rule and miss most of what they do not know.** The numbers, how they were measured and what they do not prove are in the [test set README](../cloud/category-rules-testset/README.md). Do not quote them as the accuracy for real users: they are not.

## Open questions for Aleeza

1. **Which site name does the agent pass?** The extension's `domain` is the registrable domain, so `docs.google.com` becomes `google.com`. Can the agent pass the host from the sanitized `url` (and `domain` when there is no `url`)? If not, we should drop the host-specific rules and accept a small loss.
2. **App names.** Does the agent report `Code.exe` or `Code`? The loader accepts both and any letter case. Process names **with spaces** (for example "Docker Desktop") can not be written in the file; the summary contract rejects spaces too. Do you expect many such apps, and would you rather allow spaces?
3. **Browsers.** The rules say `Other` for `chrome`, `msedge`, `firefox` and the like, because the real category comes from the site. When the extension has no event (extension off, a page it must not record), that browser time is `Other`. Is that what you want?
4. **`uncertain` for rules.** Contract 1 allows `uncertain: true` only with `label_source: "model"`. But "nothing matched, so `Other`" is exactly the case team decision 4 describes (cannot identify the activity). Should we change Contract 1 so that rules may set `uncertain: true` when no rule matched? This is the same gap I raised in the Contract 1 and Contract 2 reviews.
5. **Titles and paths are not used.** The rules can not tell a lecture on YouTube from a film (the test set measures this). Do you want a version 2 with optional keyword rules on the title or the path (for example `/shorts`)? They would run only on the PC.
6. **Shipping and updating.** Is the file embedded in the `.exe` (changes with an app update) or kept in `%LocalAppData%\DigitalLifeAnalyzer` (changes without one)? Should an event remember which `rules_version` labelled it? I propose no for now.
7. **Who edits content.** Is "Fatima edits content, you are told" (above) fine with you?
8. **The test set for Day 7.** The same 510 examples are meant to be the test set for choosing the on-device model. Titles are generic and only a few examples have one. Do you need more examples with titles, and in which categories?
