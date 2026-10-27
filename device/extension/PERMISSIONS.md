# Permission justifications (for store review)

Draft for the Chrome Web Store and Microsoft Edge Add-ons review. The extension asks for exactly two things.

## `tabs`

**Used for:** reading the address (URL) and title of the active tab, and noticing when the active tab changes, so the user can see how their time is split between websites.

**How it is limited:**
- The URL is cleaned inside the extension before anything is kept: scheme, domain and path only; query string, fragment, port and any `user:password@` are removed; long random-looking or sensitive path segments are replaced with `*`.
- The title is shortened, and dropped for pages with masked paths and for webmail and messaging sites.
- Nothing is recorded unless the companion Windows app confirms the user has given consent and has not paused tracking. If the app cannot be reached, nothing is recorded.
- Internal browser pages, local files and private (incognito) windows are never recorded (`"incognito": "not_allowed"`).
- No content scripts: the extension never reads what is on a page, form fields or anything typed.

## Host permission: `http://127.0.0.1:49721/*`

**Used for:** one read-only request, `GET /status`, to the companion Windows app running on the same PC, to learn whether the user has consented and whether tracking is on.

**How it is limited:** the address is the user's own computer (loopback), so no data goes to any other server. The request carries nothing about the user (only the extension's own ID in a header, so the app knows which extension is asking), and the answer is three fields: `consented`, `trackingEnabled`, `consentVersion`. The extension makes no other network request.

## Not requested

`storage`, `alarms`, `idle`, `scripting`, `activeTab`, `history`, `cookies`, `webRequest`, any website host permission, optional permissions, content scripts, externally connectable pages.

## Before submitting

- Remove the development `key` from `manifest.json` (see README) and use the store-assigned ID.
- Host the privacy policy page and link it in the listing (team decision 9).
- Re-check this file against the final manifest.
