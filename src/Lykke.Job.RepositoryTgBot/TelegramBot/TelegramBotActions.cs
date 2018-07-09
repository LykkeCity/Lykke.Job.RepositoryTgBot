using Common;
using Common.Log;
using Lykke.Job.RepositoryTgBot.Settings.JobSettings;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;

namespace Lykke.Job.RepositoryTgBot.TelegramBot
{
    public class RepoToCreate
    {
        public long ChatId { get; set; }
        public long UserId { get; set; }
        public int TeamId { get; set; }
        public string RepoName { get; set; }
        public string Description { get; set; }
        public bool AddSecurityTeam { get; set; }
        public bool AddCoreTeam { get; set; }
        public string MenuAction { get; set; }
    }

    public class BranchProtectionRequiredReviewsUpdateExtention : BranchProtectionRequiredReviewsUpdate
    {
        public int RequiredApprovingReviewCount { get; set; }

        public BranchProtectionRequiredReviewsUpdateExtention(BranchProtectionRequiredReviewsDismissalRestrictionsUpdate dismissalRestrictions, bool dismissStaleReviews, bool requireCodeOwnerReviews, int _requiredApprovingReviewCount) : base(dismissalRestrictions, dismissStaleReviews, requireCodeOwnerReviews)
        {
            RequiredApprovingReviewCount = _requiredApprovingReviewCount;
        }
    }

    public class TelegramBotActions
    {
        private static GitHubClient client = new GitHubClient(new ProductHeaderValue(RepositoryTgBotJobSettings.BotName));

        private static string _organisation;
        private static string _devTeam = RepositoryTgBotJobSettings.CommonDevelopersTeam;

        public TelegramBotActions(string organisation, string token)
        {
            _organisation = organisation.ToLower().Replace(' ', '-');
            var tokenAuth = new Credentials(token);
            client.Credentials = tokenAuth;
        }

        public async Task<List<Team>> GetTeamsAsync()
        {
            try
            {
                // await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.GetTeamsAsync", "Get teams async", "");

                var teams = await client.Organization.Team.GetAll(_organisation);

                var teamsList = new List<Team>();

                foreach (var team in teams)
                {
                    if (team.Name != _devTeam)
                    {
                        teamsList.Add(team);
                    }
                }
                return teamsList;
            }
            catch (Exception ex)
            {
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.GetTeamsAsync", "", ex);
                throw;
            }
        }

        public async Task<bool> RepositoryIsExist(string RepoName)
        {
            try
            {
                await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.RepositoryIsExist", "Check if repository exists", RepoName);

                var repo = await client.Repository.Get(_organisation, RepoName);

                return (repo != null);
            }
            catch (Exception e)
            {
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.RepositoryIsExist", RepoName, e);
                return false;
            }
        }

        public async Task<List<Team>> UserHasTeamCheckAsync(string nickName)
        {
            try
            {
                await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.UserHasTeamCheckAsync", "Check if user has team", nickName);

                var teams = await client.Organization.Team.GetAll(_organisation);

                var listTeams = new List<Team>();

                foreach (var team in teams)
                {
                    if (team.Name != _devTeam)
                    {
                        var teamCheck = await TeamMemberCheckAsync(nickName, team);

                        if (teamCheck)
                        {
                            listTeams.Add(team);
                        }
                    }
                }

                return listTeams;
            }
            catch (Exception ex)
            {
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.UserHasTeamCheckAsync", nickName, ex);
                throw;
            }
        }

        public async Task<bool> OrgMemberCheckAsync(string nickName)
        {
            var inOrganisation = await client.Organization.Member.CheckMember(_organisation, nickName);
            return inOrganisation;
        }

        public async Task<TelegramBotActionResult> CreateLibraryRepo(RepoToCreate repoToCreate)
        {
            try
            {
                await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.CreateLibraryRepo", "Create library repository", repoToCreate.ToJson());

                var team = await client.Organization.Team.Get(repoToCreate.TeamId);
                var teamName = team.Name.ToLower().Replace(' ', '-');

                var codeOwnersFile = (team != null) ? $"* @{_organisation}/{teamName} " : "* ";
                var commonDevelopersTeam = await GetTeamByName(RepositoryTgBotJobSettings.CommonDevelopersTeam);

                var message = $"Library repository \"{repoToCreate.RepoName}\" successfully created.";
                // Creating new repo
                var newRepo = new NewRepository(repoToCreate.RepoName) { AutoInit = true, Description = repoToCreate.Description };

                var repositoryToEdit = await client.Repository.Create(_organisation, newRepo);

                await client.Organization.Team.AddRepository(commonDevelopersTeam.Id, _organisation, repositoryToEdit.Name, new RepositoryPermissionRequest(Permission.Push));

                var branchTeams = new BranchProtectionTeamCollection();

                if (team != null)
                {
                    branchTeams.Add(teamName);
                    message += $"\n Teams: \n \"{team.Name}\"";
                }

                var architectureTeam = RepositoryTgBotJobSettings.ArchitectureTeam.ToLower().Replace(' ', '-');
                branchTeams.Add(architectureTeam);
                codeOwnersFile += $"@{_organisation}/{architectureTeam} ";
                message += $"\n \"{RepositoryTgBotJobSettings.ArchitectureTeam}\"";

                //creating Code Owners file
                await client.Repository.Content.CreateFile(repositoryToEdit.Id, "CODEOWNERS", new CreateFileRequest("Added CODEOWNERS file", codeOwnersFile));

                var protSett = new BranchProtectionSettingsUpdate(null, new BranchProtectionRequiredReviewsUpdateExtention(new BranchProtectionRequiredReviewsDismissalRestrictionsUpdate(false), true, true, 2), new BranchProtectionPushRestrictionsUpdate(branchTeams), true);

                //this method throws null reference ecxeption because responce
                try
                {
                    await client.Connection.Put<BranchProtectionSettingsUpdate>(ApiUrls.RepoBranchProtection(repositoryToEdit.Id, "master"), protSett, null, "application/vnd.github.luke-cage-preview+json");

                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                var link = repositoryToEdit.CloneUrl;

                message += "\n Clone url: " + link;


                return new TelegramBotActionResult { Success = true, Message = message };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.CreateLibraryRepo", repoToCreate.ToJson(), ex);
                return new TelegramBotActionResult { Success = false, Message = ex.Message };
            }
        }

        public async Task<TelegramBotActionResult> CreateRepo(RepoToCreate repoToCreate)
        {
            try
            {
                await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.CreateRepo", "Create repository", repoToCreate.ToJson());

                var team = await client.Organization.Team.Get(repoToCreate.TeamId);
                var teamName = team.Name.ToLower().Replace(' ', '-');

                var codeOwnersFile = (team != null) ? $"* @{_organisation}/{teamName} " : "* ";
                var commonDevelopersTeam = await GetTeamByName(RepositoryTgBotJobSettings.CommonDevelopersTeam);

                var message = $"Repository \"{repoToCreate.RepoName}\" successfully created.";
                //Creating new repo
                var newRepo = new NewRepository(repoToCreate.RepoName) { AutoInit = true, TeamId = commonDevelopersTeam.Id, Description = repoToCreate.Description };

                var repositoryToEdit = await client.Repository.Create(_organisation, newRepo);

                await client.Organization.Team.AddRepository(commonDevelopersTeam.Id, _organisation, repositoryToEdit.Name, new RepositoryPermissionRequest(Permission.Push));

                var branchTeams = new BranchProtectionTeamCollection();
                if (team != null)
                {
                    branchTeams.Add(teamName);
                    message += $"\n Teams: \n \"{team.Name}\"";
                }
                if (repoToCreate.AddSecurityTeam)
                {
                    var securityTeam = RepositoryTgBotJobSettings.SecurityTeam.ToLower().Replace(' ', '-');
                    branchTeams.Add(securityTeam);
                    codeOwnersFile += $"@{_organisation}/{securityTeam} ";
                    message += $"\n \"{RepositoryTgBotJobSettings.SecurityTeam}\"";
                }
                if (repoToCreate.AddCoreTeam)
                {
                    var coreTeam = RepositoryTgBotJobSettings.CoreTeam.ToLower().Replace(' ', '-');
                    branchTeams.Add(coreTeam);
                    codeOwnersFile += $"@{_organisation}/{coreTeam} ";
                    message += $"\n \"{RepositoryTgBotJobSettings.CoreTeam}\"";
                }

                //creating Code Owners file
                await client.Repository.Content.CreateFile(repositoryToEdit.Id, "CODEOWNERS", new CreateFileRequest("Added CODEOWNERS file", codeOwnersFile));

                //creating "dev" and "test" branches from master
                var masterSha = await client.Repository.Commit.GetSha1(repositoryToEdit.Id, "refs/heads/master");

                await client.Git.Reference.Create(repositoryToEdit.Id, new NewReference("refs/heads/dev", masterSha));
                await client.Git.Reference.Create(repositoryToEdit.Id, new NewReference("refs/heads/test", masterSha));

                //Permitions to "test" branch
                var masterProtSett = new BranchProtectionSettingsUpdate(new BranchProtectionPushRestrictionsUpdate(new BranchProtectionTeamCollection(new List<string>() { teamName })));
                var testProtSett = new BranchProtectionSettingsUpdate(null, new BranchProtectionRequiredReviewsUpdate(new BranchProtectionRequiredReviewsDismissalRestrictionsUpdate(false), true, true), new BranchProtectionPushRestrictionsUpdate(branchTeams), true);
                await client.Repository.Branch.UpdateBranchProtection(repositoryToEdit.Id, "test", testProtSett);
                await client.Repository.Branch.UpdateBranchProtection(repositoryToEdit.Id, "master", masterProtSett);

                var link = repositoryToEdit.CloneUrl;
                message += "\n Clone url: " + link;

                return new TelegramBotActionResult { Success = true, Message = message };
            }
            catch (Exception ex)
            {
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.CreateRepo", repoToCreate.ToJson(), ex);
                return new TelegramBotActionResult { Success = false, Message = ex.Message };
            }

        }

        /// <summary>
        /// Returns string with result as answer
        /// </summary>
        /// <param name="nickName"></param>
        /// <param name="teamId"></param>
        /// <returns> </returns>
        public async Task<TelegramBotActionResult> AddUserInTeam(string nickName, int teamId)
        {
            var data = new { nickName, teamId }.ToJson();
            try
            {
                await TelegramBotService._log.WriteInfoAsync("TelegramBotActions.AddUserInTeam", "Add user in team", data);

                var team = await client.Organization.Team.Get(teamId);

                if (team == null)
                    //return success false with message
                    return new TelegramBotActionResult { Success = false, Message = $"There are such no teams." };

                var userInTeam = await TeamMemberCheckAsync(nickName, team);

                if (userInTeam)
                    //return success false with message
                    return new TelegramBotActionResult { Success = true, Message = $"User {nickName} alrady in team: {team.Name}." };

                await client.Organization.Team.AddOrEditMembership(team.Id, nickName, new UpdateTeamMembership(TeamRole.Member));

                //return success true with message
                return new TelegramBotActionResult { Success = true, Message = $"User {nickName} added in team {team.Name} as a member." };
            }
            catch (Exception ex)
            {
                await TelegramBotService._log.WriteErrorAsync("TelegramBotActions.AddUserInTeam", data, ex);
                return new TelegramBotActionResult { Success = false, Message = ex.Message };
            }
        }

        public async Task<string> GetTeamById(int teamId)
        {
            var team = await client.Organization.Team.Get(teamId);
            return team.Name;
        }


        private async Task<Team> GetTeamByName(string teamName)
        {
            var teams = await client.Organization.Team.GetAll(_organisation);
            return teams.FirstOrDefault(team => teamName == team.Name);
        }

        private async Task<bool> TeamMemberCheckAsync(string nickName, Team team)
        {
            var members = await client.Organization.Team.GetAllMembers(team.Id);

            return members.Any(x => x.Login.ToLower() == nickName.ToLower());
        }

        private async Task<bool> TeamRepoCheckAsync(string repoName, int teamId)
        {
            var repositories = await client.Organization.Team.GetAllRepositories(teamId);

            return repositories.Any(x => x.Name.ToLower() == repoName.ToLower());
        }
    }

    public class TelegramBotActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
