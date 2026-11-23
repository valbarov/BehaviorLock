# Security policy

BehaviorLock is a security package. A way to get past the guard is a vulnerability, and it is handled in private
until a fix is released.

## Report a vulnerability

**Do not open a public issue for a vulnerability.**

Report it in private on GitHub:
[Report a vulnerability](https://github.com/valbarov/BehaviorLock/security/advisories/new).
You find the same form on the **Security** tab of the repository, under **Advisories**.

Please include:

- The version of the package, the .NET version, and the database provider with its version.
- What the guard should have blocked or reported, and what happened.
- The smallest code or request that shows the problem. For a problem in the SQL normalizer, the SQL text and
  the settings of the database server are enough.

## What to expect

One person maintains this project. You get a first answer within 45 days.

When the report is confirmed, a fix is prepared in private and released as a new version. The advisory is
published with the release, and it names you if you want that. Please keep the details private until then.

## Supported versions

Only the latest version gets security fixes.

| Version | Supported |
|---|---|
| The latest version | Yes |
| Older versions and all previews | No |

## What is a vulnerability

A vulnerability is a way to make the guard allow or miss something that it claims to stop. For example:

- In `Enforce` mode, an access that is not in the profile runs.
- The SQL normalizer gives a command with injected code the shape of a legal command.
- A request or a setting that an attacker controls makes the guard skip a check.
- An outbound call to a host that is not in the profile is sent through a client that the guard claims to cover.
- An unknown access is neither reported nor written to the pending file.
- Text that an attacker controls makes the guard crash the app, or makes it use time or memory without a limit.

## What is not a vulnerability

- **The known limits.** The documentation lists what the guard does not cover, for example IDOR, an injection
  that keeps the shape and stays under the limits, and code outside a request. See
  [Known limits](docs/known-limits.md). Ideas to close one of these limits are welcome as a normal issue.
- **The sample API.** [samples/SampleApi](samples/SampleApi) has weaknesses on purpose: SQL injection, mass
  assignment, SSRF and a missing role check. The tests use them to show what the guard stops.
- **A profile or an approved file that allows too much.** The guard allows what these files say.
  Their review is the job of the team that uses the package.
- **An attacker who already runs code in the app process.** The guard runs in the same process, so such an
  attacker can turn it off.
