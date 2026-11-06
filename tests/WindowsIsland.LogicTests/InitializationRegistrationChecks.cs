using System.Runtime.InteropServices;
using WindowsIsland.Services;

internal static class InitializationRegistrationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var stages = new List<string>();
        var result = await InitializationRegistration.RegisterAsync(() =>
        {
            stages.Add("register");
            return Task.FromResult("requesting-user!App");
        }, () => { stages.Add("elevate"); return Task.CompletedTask; });
        check(result == "requesting-user!App" && stages.SequenceEqual(["register"]),
            "Writable package registration never requests administrative permissions");

        foreach (var denial in new Exception[] { new UnauthorizedAccessException(), new COMException("denied", unchecked((int)0x80070005)),
            new COMException("development disabled", InitializationRegistration.SideLoadingDisabledHResult),
            new InvalidOperationException("wrapped", new COMException("development disabled", InitializationRegistration.SideLoadingDisabledHResult)) })
        {
            stages.Clear();
            result = await InitializationRegistration.RegisterAsync(() =>
            {
                stages.Add("register");
                return stages.Count == 1 ? Task.FromException<string>(denial) : Task.FromResult("requesting-user!App");
            }, () => { stages.Add("elevate"); return Task.CompletedTask; });
            check(result == "requesting-user!App" && stages.SequenceEqual(["register", "elevate", "register"]),
                "Deployment access denial prepares system metadata once and retries original-user registration: " + denial.GetType().Name);
        }

        foreach (var alreadyPrepared in new[] { false, true })
        foreach (var denied in new Exception[] { new UnauthorizedAccessException(),
            new COMException("development disabled", InitializationRegistration.SideLoadingDisabledHResult) })
        {
            stages.Clear();
            var failure = await FailureAsync(() => InitializationRegistration.RegisterAsync<string>(() =>
            {
                stages.Add("register");
                return Task.FromException<string>(denied);
            }, () => { stages.Add("elevate"); return Task.CompletedTask; }, alreadyPrepared));
            check(ReferenceEquals(failure, denied) && stages.Count(stage => stage == "elevate") == (alreadyPrepared ? 0 : 1)
                && stages.Count(stage => stage == "register") == (alreadyPrepared ? 1 : 2),
                "Persistent deployment failure never repeats the permission request: prepared=" + alreadyPrepared);
        }

        stages.Clear();
        var unrelated = new InvalidOperationException("registration failed");
        var originalFailure = await FailureAsync(() => InitializationRegistration.RegisterAsync<string>(
            () => Task.FromException<string>(unrelated), () => { stages.Add("elevate"); return Task.CompletedTask; }));
        check(ReferenceEquals(originalFailure, unrelated) && stages.Count == 0,
            "Unrelated package registration errors retain their cause without requesting elevation");

        foreach (var prepareFailure in new Exception[] { new OperationCanceledException(), new IOException("preparation failed") })
        foreach (var denied in new Exception[] { new UnauthorizedAccessException(),
            new COMException("development disabled", InitializationRegistration.SideLoadingDisabledHResult) })
        {
            var registrations = 0;
            var failure = await FailureAsync(() => InitializationRegistration.RegisterAsync<string>(() =>
            {
                registrations++;
                throw denied;
            }, () => Task.FromException(prepareFailure)));
            check(ReferenceEquals(failure, prepareFailure) && registrations == 1,
                "Cancelled or failed privileged preparation leaves initialization retryable without another registration: " + prepareFailure.GetType().Name);
        }

        check(!InitializationPolicy.Enabled(null, null) && !InitializationPolicy.Enabled(null, "1")
            && !InitializationPolicy.Enabled(null, 0), "Missing or invalid package development settings require preparation");
        check(InitializationPolicy.Enabled(null, 1) && InitializationPolicy.Enabled(1, 0)
            && !InitializationPolicy.Enabled(0, 1) && !InitializationPolicy.Enabled("1", 1),
            "Managed package policy takes precedence over local development settings");
    }

    private static async Task<Exception?> FailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }
}
