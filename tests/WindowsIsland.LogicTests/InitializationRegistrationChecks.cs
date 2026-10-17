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

        foreach (var denial in new Exception[] { new UnauthorizedAccessException(), new COMException("denied", unchecked((int)0x80070005)) })
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
        {
            stages.Clear();
            var denied = new UnauthorizedAccessException();
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
        {
            var registrations = 0;
            var failure = await FailureAsync(() => InitializationRegistration.RegisterAsync<string>(() =>
            {
                registrations++;
                throw new UnauthorizedAccessException();
            }, () => Task.FromException(prepareFailure)));
            check(ReferenceEquals(failure, prepareFailure) && registrations == 1,
                "Cancelled or failed privileged preparation leaves initialization retryable without another registration: " + prepareFailure.GetType().Name);
        }
    }

    private static async Task<Exception?> FailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }
}
