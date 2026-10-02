# Security

- 🚫 **Nothing is executed from definitions.** JsonLogic is a closed set of operators. There's no scripting and no Roslyn.
- 🧨 **ReDoS-proof:** regex constraints run on `RegexOptions.NonBacktracking`. Backreferences and lookarounds are rejected on save.
- 📏 **Limits:** JSON body size (1 MB), form body size (30 MB) and JSON depth (32) by default. Bodies that aren't valid UTF-8 are rejected.
- 🧱 **Reserved prefixes:** the admin API is protected automatically, the rest via options. Clashes with the app's own endpoints are rejected.
- 🔑 **Policies:** authorization policies referenced by definitions must exist when the definition is saved.
- ⚠️ **The admin API is open by default.** Put `.RequireAuthorization(...)` on it.
