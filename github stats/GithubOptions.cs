namespace GithubStats
{
    public class GithubOptions
    {
        public string? User {get;set;}
        public string? APIKey {get;set;}
        public string? Org {get;set;}
        public string? Repo {get;set;}
        public string FQRep => $"{Org}/{Repo}";
        public string? UrlRoot {get;set;}
        public int? TeamId {get;set;}
        public string? AppName {get;set;}
        public string? OutputFileName {get;set;}
    }
}