using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Mono.Options;

using Octokit;

class MainClass {
	const string TOOLNAME = "github-issue-mover";

	static int LogError (string message)
	{
		Console.WriteLine ("❌ " + message);
		return 1;
	}
	static void LogStatusStart (string message)
	{
		Console.Write ("   " + message);
	}
	static void LogStatusEnd ()
	{
		Console.CursorLeft = 0;
		Console.Write ("✅ ");
		Console.WriteLine ();
	}

	public async static Task<int> Main (string [] args)
	{
		var show_help = false;
		var from = string.Empty;
		var to = string.Empty;
		var token = string.Empty;

		var os = new OptionSet {
			{ "h|help|?", "Show help", (v) => show_help = true },
			{ "from=", "The issue to move. Pass the complete url to the issue.", (v) => from = v },
			{ "to=", "The repository to move to. Format: org/repo (example: mono/mono)", (v) => to = v },
			{ "token=", "The personal access token to use to authorize with GitHub", (v) => token = v },
		};

		var others = os.Parse (args);
		if (others.Count > 0) {
			Console.WriteLine ("Unexpected argument: {0}", others [0]);
			return 1;
		}

		if (show_help || string.IsNullOrEmpty (from) || string.IsNullOrEmpty (to) || string.IsNullOrEmpty (token)) {
			Console.WriteLine ($"{TOOLNAME} [OPTIONS]");
			os.WriteOptionDescriptions (Console.Out);
			return 0;
		}

		Uri uri;
		try {
			uri = new Uri (from, UriKind.Absolute);
		} catch (Exception e) {
			return LogError ($"Failed to parse from url '{from}': {e.Message}");
		}
		if (uri.Host != "github.com")
			return LogError ("Only github.com issues can be moved.");

		var fromComponents = uri.LocalPath.Split (new char [] { '/' }, StringSplitOptions.RemoveEmptyEntries);
		if (fromComponents.Length != 4) {
			return LogError ($"Unknown issue url format: {from}. Expected format: https://github.com/<org>/<repo>/issues/<number>");
		} else if (fromComponents [2] != "issues") {
			return LogError ($"Not a url to an issue: {from}");
		}
		var toComponents = to.Split ('/');
		if (toComponents.Length != 2)
			return LogError ($"Invalid format for the destination repository: {to}. Expected format: <org>/<repo>");

		var fromOrg = fromComponents [0];
		var fromRepo = fromComponents [1];
		var fromNumber = int.Parse (fromComponents [3]);
		var toOrg = toComponents [0];
		var toRepo = toComponents [1];

		var client = new GitHubClient (new ProductHeaderValue (TOOLNAME));
		client.Credentials = new Credentials (token);

		// Get info
		LogStatusStart ($"Fetching repository info...");
		var user = await client.User.Current ();
		Repository sourceRepo;
		try {
			sourceRepo = await client.Repository.Get (fromOrg, fromRepo);
		} catch (NotFoundException) {
			return LogError ($"Could not find the source repository '{fromOrg}/{fromRepo}'.");
		}
		Repository targetRepo;
		try {
			targetRepo = await client.Repository.Get (toOrg, toRepo);
		} catch (NotFoundException) {
			return LogError ($"Could not find the target repository '{toOrg}/{toRepo}'.");
		}
		LogStatusEnd ();

		Console.WriteLine ("Authenticated as: {0} ({1})", user.Name, user.Login);
		var limits = client.GetLastApiInfo ().RateLimit;
		Console.WriteLine ($"    Rate limit: {limits.Limit}");
		Console.WriteLine ($"    Remaining: {limits.Remaining}");
		Console.WriteLine ($"    Reset date: {FormatDate (limits.Reset)}");

		Issue sourceIssue;
		try {
			LogStatusStart ($"Fetching issue #{fromNumber} from {fromOrg}/{fromRepo}...");
			sourceIssue = await client.Issue.Get (sourceRepo.Id, fromNumber);
			LogStatusEnd ();
		} catch (NotFoundException) {
			return LogError ($"Could not find the issue #{fromNumber} in '{fromOrg}/{fromRepo}'.");
		}
		LogStatusStart ($"Retrieving {sourceIssue.Comments} comments...");
		var sourceComments = new List<IssueComment> (await client.Issue.Comment.GetAllForIssue (sourceRepo.Id, sourceIssue.Number));
		LogStatusEnd ();

		if (sourceIssue.ClosedAt != null)
			return LogError ($"Issue #{fromNumber} is already closed.");

		// Create new issue
		var newIssue = new NewIssue (sourceIssue.Title);
		newIssue.Body =
			$"_From @{sourceIssue.User.Login} on {FormatDate (sourceIssue.CreatedAt)}_\n\n"
			+ sourceIssue.Body + "\n\n"
			+ $"_Copied from original issue {fromOrg}/{fromRepo}#{sourceIssue.Number}_";
		LogStatusStart ($"Creating new issue in {toOrg}/{toRepo}...");
		var targetIssue = await client.Issue.Create (targetRepo.Id, newIssue);
		LogStatusEnd ();

		// Copy comments
		if (sourceComments.Count > 0) {
			LogStatusStart ($"Copying {sourceComments.Count} comment(s)...\n");
			for (var i = 0; i < sourceComments.Count; i++) {
				LogStatusStart ($"  Copying comment #{i + 1}/{sourceComments.Count}...");
				var comment = sourceComments [i];
				await client.Issue.Comment.Create (targetRepo.Id, targetIssue.Number, $"_From @{comment.User.Login} on {FormatDate (comment.CreatedAt)}_\n\n" + comment.Body);
				LogStatusEnd ();
			}
			LogStatusStart ($"Copied {sourceComments.Count} comment(s) successfully");
			LogStatusEnd ();
		}

		// Create comment in original issue
		LogStatusStart ("Adding a comment in the original issue pointing to the new issue...");
		await client.Issue.Comment.Create (sourceRepo.Id, sourceIssue.Number, $"This issue was moved to {toOrg}/{toRepo}#{targetIssue.Number}");
		LogStatusEnd ();

		// Close the original issue
		LogStatusStart ("Closing the original issue...");
		var update = sourceIssue.ToUpdate ();
		update.State = ItemState.Closed;
		await client.Issue.Update (sourceRepo.Id, sourceIssue.Number, update);
		LogStatusEnd ();

		// Yay, done!
		LogStatusStart ($"Completed successfully! New issue: {targetIssue.HtmlUrl}");
		LogStatusEnd ();
		return 0;
	}

	static string FormatDate (DateTimeOffset value)
	{
		return value.ToString ("r");
	}
}
