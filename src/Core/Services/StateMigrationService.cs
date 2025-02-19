﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bit.Core.Abstractions;
using Bit.Core.Enums;
using Bit.Core.Models.Data;
using Bit.Core.Models.Domain;
using Bit.Core.Utilities;

namespace Bit.Core.Services
{
    public class StateMigrationService : IStateMigrationService
    {
        private const int StateVersion = 4;

        private readonly IStorageService _preferencesStorageService;
        private readonly IStorageService _liteDbStorageService;
        private readonly IStorageService _secureStorageService;
        private readonly SemaphoreSlim _semaphore;

        private enum Storage
        {
            LiteDb,
            Prefs,
            Secure,
        }

        public StateMigrationService(IStorageService liteDbStorageService, IStorageService preferenceStorageService,
            IStorageService secureStorageService)
        {
            _liteDbStorageService = liteDbStorageService;
            _preferencesStorageService = preferenceStorageService;
            _secureStorageService = secureStorageService;

            _semaphore = new SemaphoreSlim(1);
        }

        public async Task MigrateIfNeededAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (await IsMigrationNeededAsync())
                {
                    await PerformMigrationAsync();
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<bool> IsMigrationNeededAsync()
        {
            var lastVersion = await GetLastStateVersionAsync();
            if (lastVersion == 0)
            {
                // fresh install, set current/latest version for availability going forward
                lastVersion = StateVersion;
                await SetLastStateVersionAsync(lastVersion);
            }
            return lastVersion < StateVersion;
        }

        private async Task PerformMigrationAsync()
        {
            var lastVersion = await GetLastStateVersionAsync();
            switch (lastVersion)
            {
                case 1:
                    await MigrateFrom1To2Async();
                    goto case 2;
                case 2:
                    await MigrateFrom2To3Async();
                    goto case 3;
                case 3:
                    await MigrateFrom3To4Async();
                    break;
            }
        }

        #region v1 to v2 Migration

        private class V1Keys
        {
            internal const string EnvironmentUrlsKey = "environmentUrls";
        }

        private async Task MigrateFrom1To2Async()
        {
            // move environmentUrls from LiteDB to prefs
            var environmentUrls = await GetValueAsync<EnvironmentUrlData>(Storage.LiteDb, V1Keys.EnvironmentUrlsKey);
            if (environmentUrls == null)
            {
                throw new Exception("'environmentUrls' must be in LiteDB during migration from 1 to 2");
            }
            await SetValueAsync(Storage.Prefs, V2Keys.EnvironmentUrlsKey, environmentUrls);

            // Update stored version
            await SetLastStateVersionAsync(2);

            // Remove old data
            await RemoveValueAsync(Storage.LiteDb, V1Keys.EnvironmentUrlsKey);
        }

        #endregion

        #region v2 to v3 Migration

        private class V2Keys
        {
            internal const string SyncOnRefreshKey = "syncOnRefresh";
            internal const string VaultTimeoutKey = "lockOption";
            internal const string VaultTimeoutActionKey = "vaultTimeoutAction";
            internal const string LastActiveTimeKey = "lastActiveTime";
            internal const string BiometricUnlockKey = "fingerprintUnlock";
            internal const string ProtectedPin = "protectedPin";
            internal const string PinProtectedKey = "pinProtectedKey";
            internal const string DefaultUriMatch = "defaultUriMatch";
            internal const string DisableAutoTotpCopyKey = "disableAutoTotpCopy";
            internal const string EnvironmentUrlsKey = "environmentUrls";
            internal const string AutofillDisableSavePromptKey = "autofillDisableSavePrompt";
            internal const string AutofillBlacklistedUrisKey = "autofillBlacklistedUris";
            internal const string DisableFaviconKey = "disableFavicon";
            internal const string ThemeKey = "theme";
            internal const string ClearClipboardKey = "clearClipboard";
            internal const string PreviousPageKey = "previousPage";
            internal const string InlineAutofillEnabledKey = "inlineAutofillEnabled";
            internal const string InvalidUnlockAttempts = "invalidUnlockAttempts";
            internal const string PasswordRepromptAutofillKey = "passwordRepromptAutofillKey";
            internal const string PasswordVerifiedAutofillKey = "passwordVerifiedAutofillKey";
            internal const string MigratedFromV1 = "migratedFromV1";
            internal const string MigratedFromV1AutofillPromptShown = "migratedV1AutofillPromptShown";
            internal const string TriedV1Resync = "triedV1Resync";
            internal const string Keys_UserId = "userId";
            internal const string Keys_UserEmail = "userEmail";
            internal const string Keys_Stamp = "securityStamp";
            internal const string Keys_Kdf = "kdf";
            internal const string Keys_KdfIterations = "kdfIterations";
            internal const string Keys_EmailVerified = "emailVerified";
            internal const string Keys_ForcePasswordReset = "forcePasswordReset";
            internal const string Keys_AccessToken = "accessToken";
            internal const string Keys_RefreshToken = "refreshToken";
            internal const string Keys_LocalData = "ciphersLocalData";
            internal const string Keys_NeverDomains = "neverDomains";
            internal const string Keys_Key = "key";
            internal const string Keys_EncOrgKeys = "encOrgKeys";
            internal const string Keys_EncPrivateKey = "encPrivateKey";
            internal const string Keys_EncKey = "encKey";
            internal const string Keys_KeyHash = "keyHash";
            internal const string Keys_UsesKeyConnector = "usesKeyConnector";
            internal const string Keys_PassGenOptions = "passwordGenerationOptions";
            internal const string Keys_PassGenHistory = "generatedPasswordHistory";
        }

        private async Task MigrateFrom2To3Async()
        {
            // build account and state
            var userId = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_UserId);
            var email = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_UserEmail);
            string name = null;
            var hasPremiumPersonally = false;
            var accessToken = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_AccessToken);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var tokenService = ServiceContainer.Resolve<ITokenService>("tokenService");
                await tokenService.SetAccessTokenAsync(accessToken, true);

                if (string.IsNullOrWhiteSpace(userId))
                {
                    userId = tokenService.GetUserId();
                }
                if (string.IsNullOrWhiteSpace(email))
                {
                    email = tokenService.GetEmail();
                }
                name = tokenService.GetName();
                hasPremiumPersonally = tokenService.GetPremium();
            }
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new Exception("'userId' must be in LiteDB during migration from 2 to 3");
            }

            var kdfType = await GetValueAsync<int?>(Storage.LiteDb, V2Keys.Keys_Kdf);
            var kdfIterations = await GetValueAsync<int?>(Storage.LiteDb, V2Keys.Keys_KdfIterations);
            var stamp = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_Stamp);
            var emailVerified = await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.Keys_EmailVerified);
            var refreshToken = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_RefreshToken);
            var account = new Account(
                new Account.AccountProfile()
                {
                    UserId = userId,
                    Email = email,
                    Name = name,
                    Stamp = stamp,
                    KdfType = (KdfType?)kdfType,
                    KdfIterations = kdfIterations,
                    EmailVerified = emailVerified,
                    HasPremiumPersonally = hasPremiumPersonally,
                },
                new Account.AccountTokens()
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                }
            );
            var environmentUrls = await GetValueAsync<EnvironmentUrlData>(Storage.Prefs, V2Keys.EnvironmentUrlsKey);
            var vaultTimeout = await GetValueAsync<int?>(Storage.Prefs, V2Keys.VaultTimeoutKey);
            var vaultTimeoutAction = await GetValueAsync<string>(Storage.Prefs, V2Keys.VaultTimeoutActionKey);
            account.Settings = new Account.AccountSettings()
            {
                EnvironmentUrls = environmentUrls,
                VaultTimeout = vaultTimeout,
                VaultTimeoutAction =
                    vaultTimeoutAction == "logout" ? VaultTimeoutAction.Logout : VaultTimeoutAction.Lock
            };
            var state = new State { Accounts = new Dictionary<string, Account> { [userId] = account } };
            state.ActiveUserId = userId;
            await SetValueAsync(Storage.LiteDb, Constants.StateKey, state);

            // migrate user-specific non-state data
            var syncOnRefresh = await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.SyncOnRefreshKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.SyncOnRefreshKey(userId), syncOnRefresh);
            var lastActiveTime = await GetValueAsync<long?>(Storage.Prefs, V2Keys.LastActiveTimeKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.LastActiveTimeKey(userId), lastActiveTime);
            var biometricUnlock = await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.BiometricUnlockKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.BiometricUnlockKey(userId), biometricUnlock);
            var protectedPin = await GetValueAsync<string>(Storage.LiteDb, V2Keys.ProtectedPin);
            await SetValueAsync(Storage.LiteDb, V3Keys.ProtectedPinKey(userId), protectedPin);
            var pinProtectedKey = await GetValueAsync<string>(Storage.LiteDb, V2Keys.PinProtectedKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.PinProtectedKey(userId), pinProtectedKey);
            var defaultUriMatch = await GetValueAsync<int?>(Storage.Prefs, V2Keys.DefaultUriMatch);
            await SetValueAsync(Storage.LiteDb, V3Keys.DefaultUriMatchKey(userId), defaultUriMatch);
            var disableAutoTotpCopy = await GetValueAsync<bool?>(Storage.Prefs, V2Keys.DisableAutoTotpCopyKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.DisableAutoTotpCopyKey(userId), disableAutoTotpCopy);
            var autofillDisableSavePrompt =
                await GetValueAsync<bool?>(Storage.Prefs, V2Keys.AutofillDisableSavePromptKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.AutofillDisableSavePromptKey(userId),
                autofillDisableSavePrompt);
            var autofillBlacklistedUris =
                await GetValueAsync<List<string>>(Storage.LiteDb, V2Keys.AutofillBlacklistedUrisKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.AutofillBlacklistedUrisKey(userId), autofillBlacklistedUris);
            var disableFavicon = await GetValueAsync<bool?>(Storage.Prefs, V2Keys.DisableFaviconKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.DisableFaviconKey(userId), disableFavicon);
            var theme = await GetValueAsync<string>(Storage.Prefs, V2Keys.ThemeKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.ThemeKey(userId), theme);
            var clearClipboard = await GetValueAsync<int?>(Storage.Prefs, V2Keys.ClearClipboardKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.ClearClipboardKey(userId), clearClipboard);
            var previousPage = await GetValueAsync<PreviousPageInfo>(Storage.LiteDb, V2Keys.PreviousPageKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.PreviousPageKey(userId), previousPage);
            var inlineAutofillEnabled = await GetValueAsync<bool?>(Storage.Prefs, V2Keys.InlineAutofillEnabledKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.InlineAutofillEnabledKey(userId), inlineAutofillEnabled);
            var invalidUnlockAttempts = await GetValueAsync<int?>(Storage.Prefs, V2Keys.InvalidUnlockAttempts);
            await SetValueAsync(Storage.LiteDb, V3Keys.InvalidUnlockAttemptsKey(userId), invalidUnlockAttempts);
            var passwordRepromptAutofill =
                await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.PasswordRepromptAutofillKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.PasswordRepromptAutofillKey(userId),
                passwordRepromptAutofill);
            var passwordVerifiedAutofill =
                await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.PasswordVerifiedAutofillKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.PasswordVerifiedAutofillKey(userId),
                passwordVerifiedAutofill);
            var cipherLocalData = await GetValueAsync<Dictionary<string, Dictionary<string, object>>>(Storage.LiteDb,
                V2Keys.Keys_LocalData);
            await SetValueAsync(Storage.LiteDb, V3Keys.LocalDataKey(userId), cipherLocalData);
            var neverDomains = await GetValueAsync<HashSet<string>>(Storage.LiteDb, V2Keys.Keys_NeverDomains);
            await SetValueAsync(Storage.LiteDb, V3Keys.NeverDomainsKey(userId), neverDomains);
            var key = await GetValueAsync<string>(Storage.Secure, V2Keys.Keys_Key);
            await SetValueAsync(Storage.Secure, V3Keys.KeyKey(userId), key);
            var encOrgKeys = await GetValueAsync<Dictionary<string, string>>(Storage.LiteDb, V2Keys.Keys_EncOrgKeys);
            await SetValueAsync(Storage.LiteDb, V3Keys.EncOrgKeysKey(userId), encOrgKeys);
            var encPrivateKey = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_EncPrivateKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.EncPrivateKeyKey(userId), encPrivateKey);
            var encKey = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_EncKey);
            await SetValueAsync(Storage.LiteDb, V3Keys.EncKeyKey(userId), encKey);
            var keyHash = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_KeyHash);
            await SetValueAsync(Storage.LiteDb, V3Keys.KeyHashKey(userId), keyHash);
            var usesKeyConnector = await GetValueAsync<bool?>(Storage.LiteDb, V2Keys.Keys_UsesKeyConnector);
            await SetValueAsync(Storage.LiteDb, V3Keys.UsesKeyConnectorKey(userId), usesKeyConnector);
            var passGenOptions =
                await GetValueAsync<PasswordGenerationOptions>(Storage.LiteDb, V2Keys.Keys_PassGenOptions);
            await SetValueAsync(Storage.LiteDb, V3Keys.PassGenOptionsKey(userId), passGenOptions);
            var passGenHistory =
                await GetValueAsync<List<GeneratedPasswordHistory>>(Storage.LiteDb, V2Keys.Keys_PassGenHistory);
            await SetValueAsync(Storage.LiteDb, V3Keys.PassGenHistoryKey(userId), passGenHistory);

            // migrate global non-state data
            await SetValueAsync(Storage.Prefs, V3Keys.PreAuthEnvironmentUrlsKey, environmentUrls);

            // Update stored version
            await SetLastStateVersionAsync(3);

            // Remove old data
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_UserId);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_UserEmail);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_AccessToken);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_RefreshToken);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_Kdf);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_KdfIterations);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_Stamp);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_EmailVerified);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_ForcePasswordReset);
            await RemoveValueAsync(Storage.Prefs, V2Keys.EnvironmentUrlsKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.PinProtectedKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.VaultTimeoutKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.VaultTimeoutActionKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.SyncOnRefreshKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.LastActiveTimeKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.BiometricUnlockKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.ProtectedPin);
            await RemoveValueAsync(Storage.Prefs, V2Keys.DefaultUriMatch);
            await RemoveValueAsync(Storage.Prefs, V2Keys.DisableAutoTotpCopyKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.AutofillDisableSavePromptKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.AutofillBlacklistedUrisKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.DisableFaviconKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.ThemeKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.ClearClipboardKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.PreviousPageKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.InlineAutofillEnabledKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.InvalidUnlockAttempts);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.PasswordRepromptAutofillKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.PasswordVerifiedAutofillKey);
            await RemoveValueAsync(Storage.Prefs, V2Keys.MigratedFromV1);
            await RemoveValueAsync(Storage.Prefs, V2Keys.MigratedFromV1AutofillPromptShown);
            await RemoveValueAsync(Storage.Prefs, V2Keys.TriedV1Resync);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_LocalData);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_NeverDomains);
            await RemoveValueAsync(Storage.Secure, V2Keys.Keys_Key);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_EncOrgKeys);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_EncPrivateKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_EncKey);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_KeyHash);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_UsesKeyConnector);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_PassGenOptions);
            await RemoveValueAsync(Storage.LiteDb, V2Keys.Keys_PassGenHistory);
        }

        #endregion

        #region v3 to v4 Migration

        private class V3Keys
        {
            internal const string PreAuthEnvironmentUrlsKey = "preAuthEnvironmentUrls";
            internal static string LocalDataKey(string userId) => $"ciphersLocalData_{userId}";
            internal static string NeverDomainsKey(string userId) => $"neverDomains_{userId}";
            internal static string KeyKey(string userId) => $"key_{userId}";
            internal static string EncOrgKeysKey(string userId) => $"encOrgKeys_{userId}";
            internal static string EncPrivateKeyKey(string userId) => $"encPrivateKey_{userId}";
            internal static string EncKeyKey(string userId) => $"encKey_{userId}";
            internal static string KeyHashKey(string userId) => $"keyHash_{userId}";
            internal static string PinProtectedKey(string userId) => $"pinProtectedKey_{userId}";
            internal static string PassGenOptionsKey(string userId) => $"passwordGenerationOptions_{userId}";
            internal static string PassGenHistoryKey(string userId) => $"generatedPasswordHistory_{userId}";
            internal static string LastActiveTimeKey(string userId) => $"lastActiveTime_{userId}";
            internal static string InvalidUnlockAttemptsKey(string userId) => $"invalidUnlockAttempts_{userId}";
            internal static string InlineAutofillEnabledKey(string userId) => $"inlineAutofillEnabled_{userId}";
            internal static string AutofillDisableSavePromptKey(string userId) => $"autofillDisableSavePrompt_{userId}";
            internal static string AutofillBlacklistedUrisKey(string userId) => $"autofillBlacklistedUris_{userId}";
            internal static string ClearClipboardKey(string userId) => $"clearClipboard_{userId}";
            internal static string SyncOnRefreshKey(string userId) => $"syncOnRefresh_{userId}";
            internal static string DefaultUriMatchKey(string userId) => $"defaultUriMatch_{userId}";
            internal static string DisableAutoTotpCopyKey(string userId) => $"disableAutoTotpCopy_{userId}";
            internal static string PreviousPageKey(string userId) => $"previousPage_{userId}";

            internal static string PasswordRepromptAutofillKey(string userId) =>
                $"passwordRepromptAutofillKey_{userId}";

            internal static string PasswordVerifiedAutofillKey(string userId) =>
                $"passwordVerifiedAutofillKey_{userId}";

            internal static string UsesKeyConnectorKey(string userId) => $"usesKeyConnector_{userId}";
            internal static string ProtectedPinKey(string userId) => $"protectedPin_{userId}";
            internal static string BiometricUnlockKey(string userId) => $"biometricUnlock_{userId}";
            internal static string ThemeKey(string userId) => $"theme_{userId}";
            internal static string AutoDarkThemeKey(string userId) => $"autoDarkTheme_{userId}";
            internal static string DisableFaviconKey(string userId) => $"disableFavicon_{userId}";
        }

        private async Task MigrateFrom3To4Async()
        {
            var state = await GetValueAsync<State>(Storage.LiteDb, Constants.StateKey);
            if (state?.Accounts is null)
            {
                // Update stored version
                await SetLastStateVersionAsync(4);
                return;
            }

            string firstUserId = null;

            // move values from state to standalone values in LiteDB
            foreach (var account in state.Accounts.Where(a => a.Value?.Profile?.UserId != null))
            {
                var userId = account.Value.Profile.UserId;
                if (firstUserId == null)
                {
                    firstUserId = userId;
                }
                var vaultTimeout = account.Value.Settings?.VaultTimeout;
                await SetValueAsync(Storage.LiteDb, V4Keys.VaultTimeoutKey(userId), vaultTimeout);

                var vaultTimeoutAction = account.Value.Settings?.VaultTimeoutAction;
                await SetValueAsync(Storage.LiteDb, V4Keys.VaultTimeoutActionKey(userId), vaultTimeoutAction);

                var screenCaptureAllowed = account.Value.Settings?.ScreenCaptureAllowed;
                await SetValueAsync(Storage.LiteDb, V4Keys.ScreenCaptureAllowedKey(userId), screenCaptureAllowed);
            }

            // use values from first userId to apply globals
            if (firstUserId != null)
            {
                var theme = await GetValueAsync<string>(Storage.LiteDb, V3Keys.ThemeKey(firstUserId));
                await SetValueAsync(Storage.LiteDb, V4Keys.ThemeKey, theme);

                var autoDarkTheme = await GetValueAsync<string>(Storage.LiteDb, V3Keys.AutoDarkThemeKey(firstUserId));
                await SetValueAsync(Storage.LiteDb, V4Keys.AutoDarkThemeKey, autoDarkTheme);

                var disableFavicon = await GetValueAsync<bool?>(Storage.LiteDb, V3Keys.DisableFaviconKey(firstUserId));
                await SetValueAsync(Storage.LiteDb, V4Keys.DisableFaviconKey, disableFavicon);
            }

            // Update stored version
            await SetLastStateVersionAsync(4);

            // Remove old data
            foreach (var account in state.Accounts)
            {
                var userId = account.Value?.Profile?.UserId;
                if (userId != null)
                {
                    await RemoveValueAsync(Storage.LiteDb, V3Keys.ThemeKey(userId));
                    await RemoveValueAsync(Storage.LiteDb, V3Keys.AutoDarkThemeKey(userId));
                    await RemoveValueAsync(Storage.LiteDb, V3Keys.DisableFaviconKey(userId));
                }
            }

            // Removal of old state data will happen organically as state is rebuilt in app
        }

        private class V4Keys
        {
            internal static string VaultTimeoutKey(string userId) => $"vaultTimeout_{userId}";
            internal static string VaultTimeoutActionKey(string userId) => $"vaultTimeoutAction_{userId}";
            internal static string ScreenCaptureAllowedKey(string userId) => $"screenCaptureAllowed_{userId}";
            internal const string ThemeKey = "theme";
            internal const string AutoDarkThemeKey = "autoDarkTheme";
            internal const string DisableFaviconKey = "disableFavicon";
        }

        #endregion

        // Helpers

        private async Task<int> GetLastStateVersionAsync()
        {
            var lastVersion = await GetValueAsync<int?>(Storage.Prefs, Constants.StateVersionKey);
            if (lastVersion != null)
            {
                return lastVersion.Value;
            }

            // check for v1 state 
            var v1EnvUrls = await GetValueAsync<EnvironmentUrlData>(Storage.LiteDb, V1Keys.EnvironmentUrlsKey);
            if (v1EnvUrls != null)
            {
                // environmentUrls still in LiteDB (never migrated to prefs), this is v1
                return 1;
            }

            // check for v2 state
            var v2UserId = await GetValueAsync<string>(Storage.LiteDb, V2Keys.Keys_UserId);
            if (v2UserId != null)
            {
                // standalone userId still exists (never moved to Account object), this is v2
                return 2;
            }

            // this is a fresh install
            return 0;
        }

        private async Task SetLastStateVersionAsync(int value)
        {
            await SetValueAsync(Storage.Prefs, Constants.StateVersionKey, value);
        }

        private async Task<T> GetValueAsync<T>(Storage storage, string key)
        {
            var value = await GetStorageService(storage).GetAsync<T>(key);
            return value;
        }

        private async Task SetValueAsync<T>(Storage storage, string key, T value)
        {
            if (value == null)
            {
                await RemoveValueAsync(storage, key);
                return;
            }
            await GetStorageService(storage).SaveAsync(key, value);
        }

        private async Task RemoveValueAsync(Storage storage, string key)
        {
            await GetStorageService(storage).RemoveAsync(key);
        }

        private IStorageService GetStorageService(Storage storage)
        {
            switch (storage)
            {
                case Storage.Secure:
                    return _secureStorageService;
                case Storage.Prefs:
                    return _preferencesStorageService;
                default:
                    return _liteDbStorageService;
            }
        }
    }
}
