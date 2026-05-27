# Novarad password storage — reverse-engineered

**Sources (read-only):**
- `F:\iPro\Novarad Analysis\Decompiled\NovaPacs\Server\_single\Novarad.Security.Server.cs`
  - `EncodingHelper.EncodeData(...)` ~ line 7211
  - `Authenticate(...)` ~ line 5802
  - `IUserData.UpdatePassword(...)` ~ line 11431
- `F:\iPro\Novarad Analysis\Decompiled\NovaPacs\Server\_single\Novarad.Utilities.Cryptography.cs`
  - `Encryption.GetHash`, `Encryption.Encrypt`, `Encryption.GetNewSaltValue`, `Encryption.StringToByte` ~ lines 268–409
- `F:\iPro\Novarad Analysis\Documents\novarad_database`
  - `shared.users` table ~ line 20356
  - `shared.user_select_pre_authenticate` ~ line 20049
  - `shared.users_select_validate_password` ~ line 20987
  - `shared.user_change_password` ~ line 19950

## Authentication flow (Novarad)

1. `UsersSelectForAuthenticationAsync(userName)` calls stored procedure
   `shared.user_select_pre_authenticate(prm_user_name)` which returns the
   `password_salt`, `password_format`, lockout state, etc. for the user.
2. Caller encodes the cleartext **evidence** (the password the user typed) using
   `EncodingHelper.EncodeData(evidence, passwordFormat, salt)`. The output is a
   STRING — Novarad's `shared.users.password` column is type `public.citext` (a
   case-insensitive text column).
3. `UsersSelectValidatePasswordAsync(userId, encodedPassword)` calls stored
   procedure `shared.users_select_validate_password(prm_user_id, prm_password)`
   which executes:
   ```sql
   SELECT user_id FROM shared.users
   WHERE user_id = prm_user_id AND password = prm_password
   ```
   `=` on `citext` is case-insensitive. If the row matches, the proc returns the
   user_id; otherwise it returns NULL.
4. Higher-level wrapper `UsersAuthenticateCustomAsync` does the same work but
   also (a) increments `failed_password_attempt_count` on miss, (b) resets it
   and updates `last_login_date` on hit, (c) creates an `active_session` row.
   We reproduce its behavior in `RadiologyPlus.NovaradAuth` for the parts we
   care about (lockout + last_login); we do NOT create Novarad sessions.

## The `password_format` discriminator

`shared.users.password_format` is an integer. `EncodingHelper.EncodeData` has
this switch:

```csharp
return passwordFormat switch
{
    0 => password,                                                       // plaintext (citext compare)
    1 => BitConverter.ToString(Encryption.GetHash(password)),            // SHA-256 hex, hyphen-separated, uppercase
    2 => Encryption.Encrypt(password, _securitySettings.EncryptionKey,
                            passwordSalt),                                // AES (Rijndael) hex, hyphen-separated, uppercase
    _ => throw new ApplicationException("Unsupported password format."),
};
```

### Format 0 — plaintext

The stored `password` column literally contains the user's cleartext password.
The `citext` column does a case-insensitive compare on login, which means **the
account password is also case-insensitive**.

### Format 1 — SHA-256

`Encryption.GetHash(string message)` computes:

```csharp
SHA256Managed.ComputeHash(Encoding.Unicode.GetBytes(message))
```

`BitConverter.ToString(byte[])` produces upper-case hex with `-` separators
(e.g. `5C-F8-9A-...`). Result is a 32-byte hash → 95-character string. No
`password_salt` is used. Format 1 is an unsalted SHA-256 of the UTF-16LE
encoded password.

Stored value is compared case-insensitively (`citext`), but since hex output
is always uppercase the case-insensitivity is a no-op.

### Format 2 — AES (Rijndael) — reversible

`Encryption.Encrypt(string message, string password, byte[] passwordSalt)`:

```csharp
byte[] bytes = Encoding.Unicode.GetBytes(message);            // UTF-16LE plaintext
byte[] hash  = Encryption.GetHash(password);                  // SHA-256(UTF-16LE(securitySettings.EncryptionKey))
bytes = Encrypt(bytes, hash, passwordSalt);                   // RijndaelManaged, key=hash (32 bytes), IV=passwordSalt (16 bytes)
return BitConverter.ToString(bytes);                          // uppercase hex with '-' separators
```

Where `Encrypt(byte[] data, byte[] password, byte[] passwordSalt)` is:

```csharp
RijndaelManaged val = new RijndaelManaged();            // default: 128-bit blocks, CBC, PKCS7
CryptoStream cs = new CryptoStream(
    memoryStream,
    val.CreateEncryptor(password, passwordSalt),        // key=password (32B from SHA-256), IV=passwordSalt (16B)
    CryptoStreamMode.Write);
cs.Write(data, 0, data.Length);
cs.FlushFinalBlock();
return memoryStream.ToArray();
```

Key derivation: `Encryption.GetHash(_securitySettings.EncryptionKey)` =
`SHA256(UTF-16LE bytes of the system encryption key)`. The key is a **system
setting**, read from the `shared.settings` table by name `"EncryptionKey"`
(see `SecuritySettingsProvider.EncryptionKey` and `GetSecuritySettingHelper<T>`
which queries `MainSettingsContext.Settings`). It is per-Novarad-install, not
per-user.

The 16-byte `password_salt` column is stored as a hyphen-separated hex string
(round-tripped through `Encryption.StringToByte` on the way in and
`BitConverter.ToString` on the way out). When the application creates a new
salt it uses `RijndaelManaged.GenerateIV()` (= 16 random bytes).

### Salt encoding

`password_salt` is **NOT base64** and is **NOT raw hex**. It is
`BitConverter.ToString(byte[])` output — uppercase hex pairs separated by
hyphens, e.g. `A3-7F-...-2C`. Decode with `StringToByte` which splits on `-`
and parses each pair as hex. Some legacy rows may store the salt as base64
(`Convert.FromBase64String`) — `StringToByte` checks for `-` and falls back to
base64 when absent.

## What we need from Novarad's database to log a user in

| What | Where |
|---|---|
| The encryption key | `shared.settings WHERE name = 'EncryptionKey'` (string value) |
| Per-user format & salt & stored password | `shared.users` row (or call `shared.user_select_pre_authenticate(user_name)` for the auth-specific subset) |
| LDAP / AD branch | `shared.users.is_ldap_user` or `shared.users.use_ad_authentication` is true |
| Special-user gates | `anonymous`, `is_visible`, `is_vendor` |
| Role | `shared.users_in_roles` ⨝ `shared.roles` |
| Facilities | `shared.user_facilities` |
| Lockout shared state | `account_is_locked`, `failed_password_attempt_count`, `failed_password_attempt_date` |
| Track on success | `last_login_date` |

The encryption key value should be cached in memory per tenant (no point round-tripping
to the DB on every login). Cache invalidation is on tenant-config reload — not a
concern for v1.

## Radiology Plus implementation contract

`RadiologyPlus.NovaradAuth.NovaradPasswordHasher` exposes:

```csharp
public string Encode(string password, int format, byte[]? salt, string encryptionKey);
public bool Verify(string password, string storedValue, int format, byte[]? salt, string encryptionKey);
public byte[]? DecodeSalt(string? saltString);   // wraps StringToByte
public string EncodeSalt(byte[] salt);           // BitConverter.ToString-style
```

Format-specific methods are visible for testing (`EncodeFormat0/1/2`).
Comparison is case-insensitive on the encoded string to match Novarad's
`citext` semantics. All hex-output formats are uppercase regardless.

The validator (`NovaradCredentialValidator`) uses this hasher together with
the per-tenant `INovaradDbContext` connection. LDAP users are routed through
`System.DirectoryServices.Protocols` instead.

## Policy boundaries (from `decisions.md` 2026-05-11)

- **MFA: skipped.** `second_factor_data` is read but not enforced.
- **Lockout: shared with Novarad.** We `UPDATE shared.users SET failed_password_attempt_count = failed_password_attempt_count + 1, failed_password_attempt_date = NOW()` on miss; on success we `SET failed_password_attempt_count = 0, last_login_date = NOW()`. We compare against a Radiology-Plus-local threshold of 5 (Novarad's threshold lives in a SiteServer setting we cannot read).
- **`anonymous = true`** → reject (kiosk / CD-viewer accounts).
- **`is_visible = false`** → reject (soft-hidden accounts).
- **`is_vendor = true`** → mapped to `Role.Tech` by default (overridable later via a per-tenant override table; not yet implemented).
- **`is_ldap_user = true` OR `use_ad_authentication = true`** → LDAP branch (do not hash).
- **`password_requires_change = true`** → not enforced in v1. Return success but include a future `MustChangePassword` flag (not yet plumbed into the contract).

## Risks / open notes

1. **Forward-compat:** If Novarad introduces a new `password_format` value
   (e.g. bcrypt), `EncodeData` will throw `ApplicationException("Unsupported
   password format.")`. We throw the same so a downstream alert fires; this
   means logins will start failing the moment any user's password is migrated.
   Update `NovaradPasswordHasher` if/when that happens.
2. **Encryption key rotation:** The Novarad setting can be rotated, which
   means cached values can go stale. Detect by catching decryption failures
   and re-reading the setting; restart of Radiology Plus is acceptable in v1.
3. **CipherMode / Padding:** `RijndaelManaged` defaults are CBC + PKCS7. We
   reproduce these explicitly on our `Aes` instance so the assumption is
   visible in code, not implicit.
4. **Encoding.Unicode in .NET = UTF-16 little-endian with no BOM.** Same on
   .NET 10 as on Novarad's older framework. Documented here so a future
   reader doesn't reach for UTF-8.
