namespace WindowsIsland.Services;

internal static class InitializationRegistration
{
    public static async Task<T> RegisterAsync<T>(Func<Task<T>> register, Func<Task> prepareSystem,
        bool systemPrepared = false)
    {
        try { return await register().ConfigureAwait(false); }
        catch (Exception error) when (!systemPrepared && error.HResult == unchecked((int)0x80070005))
        {
            // Preparing the manifest does not prove that Windows can prepare its
            // own package metadata. Recover that deployment boundary once, then
            // register again in the original user's process.
            await prepareSystem().ConfigureAwait(false);
            return await register().ConfigureAwait(false);
        }
    }
}
