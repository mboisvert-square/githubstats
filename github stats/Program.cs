using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Octokit;
using System.Text;

namespace GithubStats
{
    class Program
    {
        private static GithubOptions _options = new();
        private static GitHubClient? _client = null;
        private enum SearchType
        {
            Undefined = -1,
            Author = 0,
            Commenter = 1,
            Assignee = 2,
            Mentions = 3,
            Involves = 4
        }
        
        static async Task Main(string[] args)
        {
            using IHost host = CreateHostBuilder(args).Build();
            var tokenAuth = new Credentials(_options.User, _options.APIKey);
            _client = new GitHubClient(new ProductHeaderValue(_options.AppName));
            _client.Credentials = tokenAuth;

            await OutputGithubRates();
            await GetSummary(14);
        }

        static IHostBuilder CreateHostBuilder(string[] args) => 
            Host.CreateDefaultBuilder(args).ConfigureAppConfiguration((hostingContext, configuration) => 
            {
                configuration.Sources.Clear();

                var env = hostingContext.HostingEnvironment;

                configuration
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true, true);

                var configurationRoot = configuration.Build();
                configurationRoot.GetSection(nameof(GithubOptions)).Bind(_options);
            });
        private static async Task OutputGithubRates()
        {
            if(_client == null)
            {
                throw new Exception("Null github client");
            }
            // make a call to get the user info so we can see the rate info
            await _client.User.Get(_options.User);
            var apiInfo = _client?.GetLastApiInfo();
            var rateLimit = apiInfo?.RateLimit;
            var requestsPerHour = rateLimit?.Limit;
            var requestsLeft = rateLimit?.Remaining;
            var resetTime = rateLimit?.Reset;

            Console.WriteLine($"Rate Limit: {requestsPerHour} / Requests Left: {requestsLeft} / Reset Time: {resetTime}");
        }
        private static async Task GetSummary(int daysBack)
        {
            Console.WriteLine("Getting Info");
            using var fsCsv = new FileStream($"{Path.Join(Environment.CurrentDirectory, "//")}{_options.OutputFileName}.csv", System.IO.FileMode.Create, FileAccess.Write);
            using var swCsv = new StreamWriter(fsCsv);

            using var fs = new FileStream($"{Path.Join(Environment.CurrentDirectory, "//")}{_options.OutputFileName}.txt", System.IO.FileMode.Create, FileAccess.Write);
            using var sw = new StreamWriter(fs);
            var teamMembers = await GetAllTeamMembers(_options.TeamId);
            var teamMemberIds = teamMembers.Select(x => x.Id).ToList();
            var dashes = new string('-', 80);

            sw.WriteLine(DateTime.Now);
            sw.WriteLine($"Output For PR review for the last {daysBack} days...");

            swCsv.WriteLine(DateTime.Now);
            swCsv.WriteLine($"Output For PR review for the last {daysBack} days...");

            const string HEADER = "type,creator,reviewer,state,daysopen,title,created,updated,comments";
            var sbOutCreatedCsv = new StringBuilder();
            var sbOutReviewedCsv = new StringBuilder();

            foreach(var teamMember in teamMembers)
            {
                sw.WriteLine(dashes);
                sw.WriteLine($"Team Member {teamMember.Login}");
                var authored = await GetPRs(_options.FQRep, teamMember.Login, daysBack, SearchType.Author);
                if(authored.TotalCount > 0)
                {
                    sw.WriteLine($"{teamMember.Login}: {authored.TotalCount} created ({authored.Items.Count(x=> x.State == ItemState.Open)} open / {authored.Items.Count(x=> x.State == ItemState.Closed)} closed)");

                    foreach(var r in authored.Items)
                    {
                        sw.WriteLine($"\t{r.User.Login}: {r.Title} - {r.State}");

                        var daysCalc = (r.State == ItemState.Open) 
                        ? Math.Round((DateTime.Now - r.CreatedAt).TotalDays)
                        : Math.Round((r.ClosedAt - r.CreatedAt)?.TotalDays ?? 0);
                        sbOutCreatedCsv.AppendLine($"C,{r.User.Login},,{r.State},{daysCalc},{r.Title.Replace(",", "")},{r.CreatedAt},{r.UpdatedAt},{r.Comments}");
                    }
                    sw.WriteLine();
                }

                var reviewed = await GetPRs(_options.FQRep, teamMember.Login, daysBack, SearchType.Commenter);
                if(reviewed.TotalCount > 0)
                {
                    var reviewedItems = reviewed.Items.Where(x=> x.User.Login != teamMember.Login);
                    sw.WriteLine($"{teamMember.Login}: {reviewedItems.Count()} reviewed ({reviewedItems.Count(x=> x.State == ItemState.Open)} open / {reviewedItems.Count(x=> x.State == ItemState.Closed)} closed)");

                    foreach(var r in reviewedItems)
                    {
                        var daysCalc = (r.State == ItemState.Open) 
                        ? Math.Round((DateTime.Now - r.CreatedAt).TotalDays)
                        : Math.Round((r.ClosedAt - r.CreatedAt)?.TotalDays ?? 0);

                        var daysCalcText = (r.State == ItemState.Open) 
                        ? $"Open {daysCalc} days"
                        : $"Closed in {daysCalc} days";
                        
                        sw.WriteLine($"\t{r.User.Login}: {r.Title} - {r.State} ({daysCalcText})");

                        sbOutReviewedCsv.AppendLine($"R,{r.User.Login},{teamMember.Login},{r.State},{daysCalc},{r.Title.Replace(",", "")},{r.CreatedAt},{r.UpdatedAt},{r.Comments}");
                    }
                    sw.WriteLine(dashes);
                    sw.WriteLine();
                }
            }

            swCsv.WriteLine(HEADER);
            swCsv.Write(sbOutCreatedCsv.ToString());
            swCsv.WriteLine(sbOutReviewedCsv.ToString());
        }

        private static async Task<SearchIssuesResult> GetPRs(string repo, string login, int daysBack, SearchType searchType, ItemState? state = null)
        {
            if(_client == null)
            {
                throw new Exception("Githubclient is null");
            }
            var req = new SearchIssuesRequest();
            req.Repos.Add(repo);
            req.Type = IssueTypeQualifier.PullRequest;
            var days = -1 * daysBack;
            req.Created = new DateRange(DateTimeOffset.Now.AddDays(days), SearchQualifierOperator.GreaterThan);
            switch(searchType)
            {
                case SearchType.Author:
                    req.Author = login;
                    break;
                case SearchType.Commenter:
                    req.Commenter = login;
                    break;
                case SearchType.Assignee:
                    req.Assignee = login;
                    break;
                case SearchType.Mentions:
                    req.Mentions = login;
                    break;
                case SearchType.Involves:
                    req.Involves = login;
                    break;
                default:
                    throw new Exception($"Uknownn type {searchType}");
            }

            if(state.HasValue)
            {
                req.State = state;
            }

            req.SortField = IssueSearchSort.Created;
            req.Order = SortDirection.Ascending;
            var resp = await _client.Search.SearchIssues(req);
            return resp;
        }
        
        private static async Task<IReadOnlyList<User>> GetAllTeamMembers(int? teamId)
        {
            if(!teamId.HasValue)
            {
                return await Task.FromResult(new List<User>());
            }

            var teamMembers = await GetTeamMembers(teamId.Value);
            return teamMembers;
        }

        private static async Task<int?> GetTeamId(string teamName)
        {
            if(_client == null)
            {
                throw new Exception("Githubclient is null");
            }

            var teams = await _client.Organization.Team.GetAll(_options.Org);
            var team = teams.Where(x => x.Name == teamName);
            return team?.FirstOrDefault()?.Id;
        }

        private static async Task<IReadOnlyList<User>> GetTeamMembers(int? teamId)
        {
            if(_client == null)
            {
                throw new Exception("Githubclient is null");
            }
            if(!teamId.HasValue)
            {
                return await Task.FromResult(new List<User>());
            }
            var teamMembers = await _client.Organization.Team.GetAllMembers(teamId.Value);
            return teamMembers;
        }
        
    }
}