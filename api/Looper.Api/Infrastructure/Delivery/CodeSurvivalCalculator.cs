using System.Diagnostics;

namespace Looper.Api.Infrastructure.Delivery;

public sealed record SurvivalResult(int Additions, int SurvivingAdditions, string? Error)
{
    public static SurvivalResult Failed(string error) => new(0, 0, error);
}

/// <summary>
/// Measures code survival: of the lines a merged PR added, how many are still attributed to
/// the PR's commits at HEAD (git blame). Rewritten or deleted lines stop being attributed —
/// that is exactly the "gets rewritten next sprint" signal. Sampling: the 40 files with the
/// most additions; both totals come from the same sample so the rate stays honest.
/// </summary>
public sealed class CodeSurvivalCalculator(ILogger<CodeSurvivalCalculator> logger)
{
    private const int MaxFiles = 40;
    private const int MaxPrCommits = 200;

    public async Task<SurvivalResult> ComputeAsync(string repoPath, string mergeCommitSha, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git")) && !File.Exists(Path.Combine(repoPath, ".git")))
        {
            return SurvivalResult.Failed($"{repoPath} is not a git repository.");
        }

        if (await Git(repoPath, ["cat-file", "-e", $"{mergeCommitSha}^{{commit}}"], cancellationToken) is not { ExitCode: 0 })
        {
            return SurvivalResult.Failed($"Merge commit {mergeCommitSha[..Math.Min(10, mergeCommitSha.Length)]} not found locally — pull the repository.");
        }

        // The PR's commit set: for a true merge commit, everything the second parent brought in;
        // for a squash/rebase merge, the commit itself.
        var parents = await Git(repoPath, ["rev-list", "--parents", "-n", "1", mergeCommitSha], cancellationToken);
        var isMergeCommit = parents.ExitCode == 0 && parents.Stdout.Trim().Split(' ').Length > 2;

        var prShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mergeCommitSha };
        if (isMergeCommit)
        {
            var list = await Git(repoPath, ["rev-list", $"{mergeCommitSha}^1..{mergeCommitSha}"], cancellationToken);
            foreach (var sha in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(MaxPrCommits))
            {
                prShas.Add(sha.Trim());
            }
        }

        // Per-file additions of the PR's change.
        var numstat = isMergeCommit
            ? await Git(repoPath, ["diff", "--numstat", $"{mergeCommitSha}^1", mergeCommitSha], cancellationToken)
            : await Git(repoPath, ["show", "--numstat", "--format=", mergeCommitSha], cancellationToken);
        if (numstat.ExitCode != 0)
        {
            return SurvivalResult.Failed($"git diff failed: {numstat.Stderr.Trim()}");
        }

        var files = new List<(string File, int Added)>();
        foreach (var line in numstat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 3 && int.TryParse(parts[0], out var added) && added > 0)
            {
                files.Add((parts[2].Trim(), added));
            }
        }
        var sampled = files.OrderByDescending(f => f.Added).Take(MaxFiles).ToList();
        if (sampled.Count == 0)
        {
            return new SurvivalResult(0, 0, null); // nothing added (e.g. pure deletions)
        }

        var additions = 0;
        var surviving = 0;
        foreach (var (file, added) in sampled)
        {
            additions += added;
            var blame = await Git(repoPath, ["blame", "--line-porcelain", "HEAD", "--", file], cancellationToken);
            if (blame.ExitCode != 0) continue; // file deleted or renamed away — its lines did not survive

            foreach (var line in blame.Stdout.Split('\n'))
            {
                // Porcelain header lines start with the 40-char sha.
                if (line.Length > 40 && line[40] == ' ' && prShas.Contains(line[..40]))
                {
                    surviving++;
                }
            }
        }

        logger.LogInformation("Survival for {Sha}: {Surviving}/{Additions} lines over {Files} file(s)",
            mergeCommitSha[..10], surviving, additions, sampled.Count);
        return new SurvivalResult(additions, Math.Min(surviving, additions), null);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Git(
        string repoPath, string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (-1, "", ex.Message);
        }
    }
}
