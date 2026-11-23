// spokes-vault.js
// Static JS functions for Capacitor and web vault key persistence.
// Centralizes reading/writing the encrypted chat password and calling /spokesapi/vault/* endpoints.

window.spokesVault = {

    _getCurrentOrigin: function () {
        if (window.spokesAuth && typeof window.spokesAuth.getCanonicalOrigin === 'function') {
            return window.spokesAuth.getCanonicalOrigin();
        }
        try {
            const parsed = new URL(window.location.href);
            if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
                return parsed.origin.toLowerCase();
            }
        } catch (e) {}
        return '';
    },

    /**
     * Reads the stored encrypted vault key from Capacitor Preferences.
     * Returns { key: string } if found, or null if not available.
     * On web, returns null (vault key comes from HttpOnly cookie, handled server-side).
     */
    getStoredVaultKey: async function () {
        if (window.Capacitor?.Plugins?.Preferences) {
            try {
                const currentOrigin = this._getCurrentOrigin();
                if (!currentOrigin) return null;
                const prefs = window.Capacitor.Plugins.Preferences;
                const value = window.spokesAuth?._getOriginPrefAsync
                    ? await window.spokesAuth._getOriginPrefAsync(prefs, 'chat_vault_key', currentOrigin, true)
                    : (await prefs.get({ key: 'chat_vault_key::' + currentOrigin }))?.value;

                if (value) {
                    return { key: value };
                }
            } catch (e) {
                console.warn('[Vault] Failed to read from Preferences:', e);
            }
            return null;
        }
        // Web: vault key comes from HttpOnly cookie, handled server-side via TryInitializeFromVaultCookie.
        return null;
    },

    /**
     * Stores the encrypted vault key to Capacitor Preferences.
     * Only operates on native mobile platforms.
     * @param {string} encryptedKey - The Data Protection-encrypted vault password.
     */
    storeVaultKey: async function (encryptedKey) {
        if (window.Capacitor?.Plugins?.Preferences) {
            const currentOrigin = this._getCurrentOrigin();
            if (!currentOrigin) return;
            const key = 'chat_vault_key::' + currentOrigin;
            await window.Capacitor.Plugins.Preferences.set({ key, value: encryptedKey });
        }
    },

    /**
     * Clears all stored vault keys from Capacitor Preferences for the current origin.
     */
    clearStoredVaultKey: async function () {
        if (window.spokesMediaLoader && typeof window.spokesMediaLoader.clearEncryptedCache === 'function') {
            try { await window.spokesMediaLoader.clearEncryptedCache(); } catch (e) {}
        }
        if (window.Capacitor?.isNativePlatform?.()) {
            const currentOrigin = this._getCurrentOrigin();
            if (currentOrigin) {
                await window.Capacitor.Plugins.Preferences.remove({ key: 'chat_vault_key::' + currentOrigin });
            }
        }
    },

    /**
     * Authenticates with /spokesapi/vault/login to set the HttpOnly cookie (on web),
     * mark DeviceSession.HasVaultCookie = true, and persist the encrypted key in
     * Capacitor Preferences (on native mobile).
     * @param {string} password - The user's vault password.
     * @returns {Promise<{success: boolean, vaultKey?: string}>}
     */
    login: async function (password) {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' })
                : { 'Content-Type': 'application/json' };
            const response = await fetch('/spokesapi/vault/login', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify({ Password: password })
            });
            if (!response.ok) {
                return { success: false };
            }
            const data = await response.json();
            if (data && data.vaultKey) {
                await this.storeVaultKey(data.vaultKey);
            }
            if (data && data.success !== false && window.spokesMediaLoader && typeof window.spokesMediaLoader.setVaultPersisted === 'function') {
                window.spokesMediaLoader.setVaultPersisted(true);
            }
            return data;
        } catch (e) {
            console.warn('[Vault] Login failed:', e);
            return { success: false };
        }
    },

    /**
     * Silently renews (sliding expiry) or upgrades (v1 -> v2) an active vault session via
     * /spokesapi/vault/renew without exposing the plaintext password to JavaScript.
     * @returns {Promise<{success: boolean, vaultKey?: string}>}
     */
    renew: async function () {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders()
                : {};
            const response = await fetch('/spokesapi/vault/renew', {
                method: 'POST',
                credentials: 'include',
                cache: 'no-store',
                headers: headers
            });
            if (!response.ok) {
                return { success: false };
            }
            const data = await response.json();
            if (data && data.vaultKey) {
                await this.storeVaultKey(data.vaultKey);
            }
            return data;
        } catch (e) {
            console.warn('[Vault] Renewal failed:', e);
            return { success: false };
        }
    },

    /**
     * Logs out of /spokesapi/vault/logout (deleting the HttpOnly cookie and clearing
     * DeviceSession.HasVaultCookie) and removes any stored Capacitor Preferences key.
     */
    logout: async function () {
        if (window.spokesMediaLoader && typeof window.spokesMediaLoader.clearEncryptedCache === 'function') {
            try { await window.spokesMediaLoader.clearEncryptedCache(); } catch (e) {}
        }
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders()
                : {};
            await fetch('/spokesapi/vault/logout', {
                method: 'POST',
                headers: headers
            });
        } catch (e) {
            console.warn('[Vault] Logout request failed:', e);
        }
        await this.clearStoredVaultKey();
    },

    /**
     * Queries /spokesapi/vault/status for whether the current device has an active vault cookie.
     * @returns {Promise<{hasCookie: boolean}>}
     */
    getStatus: async function () {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders()
                : {};
            const response = await fetch('/spokesapi/vault/status', {
                credentials: 'include',
                cache: 'no-store',
                headers: headers
            });
            if (response.ok) {
                return await response.json();
            }
        } catch (e) {
            console.warn('[Vault] Status check failed:', e);
        }
        return { hasCookie: false };
    }
};
