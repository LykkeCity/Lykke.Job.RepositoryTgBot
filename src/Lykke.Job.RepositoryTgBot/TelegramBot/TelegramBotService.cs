using Autofac;
using Common;
using Common.Log;
using Lykke.Job.RepositoryTgBot.AzureRepositories.TelegramBotHistory;
using Lykke.Job.RepositoryTgBot.Core.Domain.TelegramBotHistory;
using Lykke.Job.RepositoryTgBot.Settings.JobSettings;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Lykke.Job.RepositoryTgBot.TelegramBot
{
    class InlineMessage
    {
        public string Text { get; set; }
        public InlineKeyboardMarkup ReplyMarkup { get; set; }
    }

    class TimeoutHelper
    {
        public Telegram.Bot.Types.User User { get; set; }
        public long ChatId { get; set; }
    }

    public class TelegramBotService : IStopable, IStartable
    {
        #region Repositories

        private readonly ITelegramBotHistoryRepository _telegramBotHistoryRepository;

        #endregion
        public static ILog _log;
        private readonly ITelegramBotClient _bot;
        private static TelegramBotActions _actions = new TelegramBotActions(RepositoryTgBotJobSettings.OrgainzationName, RepositoryTgBotJobSettings.GitToken);

        #region Constants

        private const string _mainMenu = "Create new repository";

        private const string _createGithubRepo = "CreateGithubRepo";
        private const string _createLibraryRepo = "CreateLibraryRepo";
        private const string _resetTeam = "ResetTeam";

        private const string _chooseTeam = "What is your team?";

        private const string _questionEnterName = "Enter repository name";
        private const string _questionEnterDesc = "Enter repository description";
        private const string _questionEnterGitAcc = "Enter your GitHub account";

        private const string _questionSecurity = "Will service interact with sensitive data, finance operations or includes other security risks?";
        private const string _questionMultipleTeams = "Is it a common service which will be used by multiple teams?";
        #endregion

        private static List<Team> teams;

        private TimeoutHelper CurrentUser = new TimeoutHelper();
        private TimeoutHandler TimeoutTimer;

        public TelegramBotService(RepositoryTgBotJobSettings settings, ILog log, ITelegramBotHistoryRepository telegramBotHistoryRepository)
        {
            _telegramBotHistoryRepository = telegramBotHistoryRepository;

            _log = log;
            _bot = new TelegramBotClient(settings.BotToken);

            _bot.OnMessage += BotOnMessageReceived;
            _bot.OnMessageEdited += BotOnMessageReceived;
            _bot.OnCallbackQuery += BotOnCallbackQueryReceived;
            _bot.OnReceiveError += BotOnReceiveError;

            TimeoutTimer = new TimeoutHandler(_log);
            TimeoutTimer.Timeout += Timeout;
        }

        private async void BotOnMessageReceived(object sender, MessageEventArgs messageEventArgs)
        {
            try
            {
                if (!CheckTotalTimeLimit(messageEventArgs.Message.Date)) return;

#if DEBUG
                Console.WriteLine("BotOnMessageReceived - " + messageEventArgs.Message.Text);
#endif

                var message = messageEventArgs.Message;

                if (message == null || message.Type != MessageType.Text) return;

                await _log.WriteInfoAsync("TelegramBotService.BotOnMessageReceived", "Command received", message.ReplyToMessage?.Text);

                var firstWord = message.Text.Split(' ').First();
                var command = firstWord.IndexOf('@') == -1 ? firstWord : firstWord.Substring(0, firstWord.IndexOf('@'));

                if (command == "/groupId")
                {
                    await SendTextToUser(message.Chat.Id, $"Group Id: {message.Chat.Id}");
                    return;
                }

                var result = await CheckForGroupAccess(message.Chat.Id, message.Chat.Id);
                if (!result) return;

                // get repository name and checking user
                if (message.ReplyToMessage?.Text == $"@{message.From.Username} \n" + _questionEnterName && TimeoutTimer.Working && CurrentUser.User.Id == message.From.Id)
                {
                    TimeoutTimer.Stop();
                    var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == message.Chat.Id && x.UserId == message.From.Id);
                    if (prevQuestion != null && prevQuestion.Question == _questionEnterName)
                    {
                        if (!Regex.IsMatch(message.Text, @"^[a-zA-Z0-9._-]+$"))
                        {
                            await SendTextToUser(message.Chat.Id, $"@{message.From.Username} \n" + "Incorrect format.");
                            await _bot.SendTextMessageAsync(message.Chat.Id, $"@{message.From.Username} \n" + _questionEnterName, replyMarkup: new ForceReplyMarkup { Selective = true });
                        }
                        else
                        {
                            var repoIsAlreadyExists = await _actions.RepositoryIsExist(message.Text);
                            if (repoIsAlreadyExists)
                            {
                                await SendTextToUser(message.Chat.Id, $"@{message.From.Username} \n" + "Repository with this name already exists.");
                                await _bot.SendTextMessageAsync(message.Chat.Id, $"@{message.From.Username} \n" + _questionEnterName, replyMarkup: new ForceReplyMarkup { Selective = true });
                            }
                            else
                            {
                                await _bot.SendTextMessageAsync(message.Chat.Id, $"@{message.From.Username} \n" + _questionEnterDesc, replyMarkup: new ForceReplyMarkup { Selective = true });
                                await CreateBotHistory(message.Chat.Id, message.From.Id, message.From.Username, _questionEnterDesc, message.Text);
                            }
                        }

                        TimeoutTimer.Start();

                    }
                    else
                    {
                        await SendTextToUser(message.Chat.Id);
                    }

                }

                // get repository description and checking user
                else if (message.ReplyToMessage?.Text == $"@{message.From.Username} \n" + _questionEnterDesc && TimeoutTimer.Working && CurrentUser.User.Id == message.From.Id)
                {
                    TimeoutTimer.Stop();
                    var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == message.Chat.Id && x.UserId == message.From.Id);

                    var teamName = await GetTeamName(message.Chat.Id, message.From.Id);
                    var checkMenuType = await GetMenuAction(message.Chat.Id, message.From.Id, message.From.Username, teamName);
                    if (checkMenuType == _createLibraryRepo)
                    {
                        var question = await CreateRepoAsync(message.Chat.Id, message.From.Id);
                        await CreateBotHistory(message.Chat.Id, message.From.Id, message.From.Username, question, message.Text);
                    }
                    else if (prevQuestion != null && prevQuestion.Question == _questionEnterDesc && teamName != RepositoryTgBotJobSettings.SecurityTeam)
                    {
                        var inlineMessage = new InlineMessage
                        {
                            Text = $"@{message.From.Username} \n" + _questionSecurity,
                            ReplyMarkup = new InlineKeyboardMarkup(new[]
                            {
                            new[] // first row
                            {
                                InlineKeyboardButton.WithCallbackData("Yes", "Security"),
                                InlineKeyboardButton.WithCallbackData("No", "NoSecurity")
                            }
                        })
                        };

                        await _bot.SendTextMessageAsync(message.Chat.Id, inlineMessage.Text, replyMarkup: inlineMessage.ReplyMarkup);
                        await CreateBotHistory(message.Chat.Id, message.From.Id, message.From.Username, _questionSecurity, message.Text);

                        TimeoutTimer.Start();
                    }
                    else if (prevQuestion != null && prevQuestion.Question == _questionEnterDesc && teamName == RepositoryTgBotJobSettings.SecurityTeam)
                    {
                        CallbackQuery callbackQuery = new CallbackQuery
                        {
                            Data = "NoSecurity",
                            From = new Telegram.Bot.Types.User
                            {
                                Id = message.From.Id,
                                Username = message.From.Username
                            }
                        };

                        await CreateBotHistory(message.Chat.Id, message.From.Id, message.From.Username, _questionSecurity, message.Text);
                        await SendResponseMarkup(callbackQuery, message);
                    }
                    else
                    {
                        await SendTextToUser(message.Chat.Id);
                    }

                }

                else if (message.ReplyToMessage?.Text == $"@{message.From.Username} \n" + _questionEnterGitAcc && TimeoutTimer.Working && CurrentUser.User.Id == message.From.Id)
                {
                    TimeoutTimer.Stop();
                    var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == message.Chat.Id && x.UserId == message.From.Id);
                    if (prevQuestion != null && prevQuestion.Question == _questionEnterGitAcc)
                    {
                        if (!Regex.IsMatch(message.Text, @"^[a-zA-Z0-9._-]+$"))
                        {
                            await SendTextToUser(message.Chat.Id, $"@{message.From.Username} \n" + "Incorrect format.");
                            await _bot.SendTextMessageAsync(message.Chat.Id, $"@{message.From.Username} \n" + _questionEnterGitAcc, replyMarkup: new ForceReplyMarkup { Selective = true });
                        }
                        else
                        {
                            await SendTextToUser(message.Chat.Id, $"@{message.From.Username} \n" + "Please, wait a second...");
                            await CreateBotHistory(message.Chat.Id, message.From.Id, message.From.Username, _chooseTeam, message.Text);
                            var gitUserTeamList = await _actions.UserHasTeamCheckAsync(message.Text);

                            if (gitUserTeamList.Count == 0)
                            {
                                var inlineMessage = TeamListToSend(message.From.Username, teams, $"You not assign to any team in organisation {RepositoryTgBotJobSettings.OrgainzationName}\n");
                                await _bot.SendTextMessageAsync(message.Chat.Id, inlineMessage.Text, replyMarkup: inlineMessage.ReplyMarkup);
                            }
                            else if (gitUserTeamList.Count == 1)
                            {
                                var userTeam = gitUserTeamList.FirstOrDefault();
                                await SendTextToUser(message.Chat.Id, $"@{message.From.Username} \n" + $"Your team is \"{userTeam.Name}\".");
                                await TeamSelected(message.Chat.Id, message.From, userTeam.Id.ToString());
                            }
                            else
                            {
                                var inlineMessage = TeamListToSend(message.From.Username, gitUserTeamList, "You are assigned to multiple teams.\n");
                                await _bot.SendTextMessageAsync(message.Chat.Id, inlineMessage.Text, replyMarkup: inlineMessage.ReplyMarkup);

                            }
                        }

                        TimeoutTimer.Start();
                    }
                    else
                    {
                        await SendTextToUser(message.Chat.Id);
                    }
                }
                else if (TimeoutTimer.Working && CurrentUser.User.Id != message.From.Id)
                {
                    await SendTextToUser(message.Chat.Id, $"@{message.From.Username} Please, wait for  user @{CurrentUser.User.Username} finishes creating repository");
                }

                // read commands
                else
                {
                    if (TimeoutTimer.Working && CurrentUser.User.Id == message.From.Id)
                        TimeoutTimer.Stop();
                    switch (command)
                    {

                        // send inline menu keyboard
                        case "/create":

                            var TeamId = await GetUserTeamId(message.Chat.Id, message.From.Id);
                            string addTeam = "";
                            var inlineMessage = new InlineMessage();

                            var inlineKeyboard = new List<IEnumerable<InlineKeyboardButton>>()
                        {
                            new [] // first row
                            {
                                InlineKeyboardButton.WithCallbackData("Create Repo", _createGithubRepo),
                                InlineKeyboardButton.WithCallbackData("Create Library Repo", _createLibraryRepo)
                            }
                        };

                            if (TeamId != 0)
                            {
                                var teamName = await _actions.GetTeamById(TeamId);
                                addTeam = $"\nYour team is \"{teamName}\"";
                                inlineKeyboard.Add(
                                new[] // first row
                                {
                                InlineKeyboardButton.WithCallbackData("Reset my team", _resetTeam)
                                });
                            }



                            inlineMessage.ReplyMarkup = new InlineKeyboardMarkup(inlineKeyboard);

                            inlineMessage.Text = $"@{message.From.Username} \n" + _mainMenu + addTeam;

                            await _bot.SendTextMessageAsync(message.Chat.Id, inlineMessage.Text, replyMarkup: inlineMessage.ReplyMarkup);
                            var botHistory = new TelegramBotHistory
                            {
                                RowKey = Guid.NewGuid().ToString(),
                                ChatId = message.Chat.Id,
                                UserId = message.From.Id,
                                TelegramUserName = message.From.Username,
                                Question = inlineMessage.Text
                            };

                            await _telegramBotHistoryRepository.SaveAsync(botHistory);
                            break;

                        case "/resetMyTeam":
                            await ClearTeam(message.Chat.Id, message.From);
                            break;
                        default:
                            //send default answer
                            await SendTextToUser(message.Chat.Id);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotService.BotOnMessageReceived", messageEventArgs.Message.ReplyToMessage?.Text, ex);
                return;
            }
        }

        private async void BotOnCallbackQueryReceived(object sender, CallbackQueryEventArgs callbackQueryEventArgs)
        {
            try
            {
                if (!CheckTotalTimeLimit(callbackQueryEventArgs.CallbackQuery.Message.Date)) return;

                //Here implements actions on reciving
                var callbackQuery = callbackQueryEventArgs.CallbackQuery;

                await _log.WriteInfoAsync("TelegramBotService.BotOnCallbackQueryReceived", "Callback Query Received", callbackQuery.Data);

                var result = await CheckForGroupAccess(callbackQuery.Message.Chat.Id, callbackQuery.Message.Chat.Id);
                if (!result) return;

                if (callbackQuery.Message.Text.Contains(_chooseTeam))
                {
                    await TeamSelected(callbackQuery.Message.Chat.Id, callbackQuery.From, callbackQuery.Data);
                }
                else if (TimeoutTimer.Working && CurrentUser.User.Id != callbackQuery.From.Id)
                {
                    await SendTextToUser(callbackQuery.Message.Chat.Id, $"@{callbackQuery.From.Username} Please, wait for  user @{CurrentUser.User.Username} finishes creating repository");
                }
                else
                {
                    await SendResponseMarkup(callbackQuery, callbackQuery.Message);
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotService.BotOnCallbackQueryReceived", callbackQueryEventArgs.CallbackQuery.Message.Text, ex);
                return;
            }
        }

        private void BotOnReceiveError(object sender, ReceiveErrorEventArgs receiveErrorEventArgs)
        {
            Console.WriteLine("Received error: {0} — {1}",
                receiveErrorEventArgs.ApiRequestException.ErrorCode,
                receiveErrorEventArgs.ApiRequestException.Message);
        }

        private async Task SendResponseMarkup(CallbackQuery callbackQuery, Message message)
        {
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.SendResponseMarkup", "Send markup to user", "");

                var historyCreateSkipped = false;
                if (TimeoutTimer.Working && CurrentUser.User.Id != callbackQuery.From.Id)
                {
                    await SendTextToUser(callbackQuery.Message.Chat.Id, $"@{callbackQuery.From.Username} Please, wait for  user @{CurrentUser.User.Username} finishes creating repository");
                }
                else
                {
                    var inlineMessage = new InlineMessage();
                    var question = String.Empty;

                    switch (callbackQuery.Data)
                    {
                        case _createGithubRepo:
                        case _createLibraryRepo:

                            TimeoutTimer.Stop();
                            //Getting userTeamId
                            var userTeamId = await GetUserTeamId(message.Chat.Id, callbackQuery.From.Id);

                            if (userTeamId != 0)
                            {
                                question = _questionEnterName;
                                await _bot.SendTextMessageAsync(message.Chat.Id, $"@{callbackQuery.From.Username} \n" + _questionEnterName, replyMarkup: new ForceReplyMarkup { Selective = true });
                            }
                            else
                            {
                                question = _questionEnterGitAcc;
                                await _bot.SendTextMessageAsync(message.Chat.Id, $"@{callbackQuery.From.Username} \n" + _questionEnterGitAcc, replyMarkup: new ForceReplyMarkup { Selective = true });
                            }

                            CurrentUser.User = callbackQuery.From;
                            CurrentUser.ChatId = message.Chat.Id;
                            TimeoutTimer.Start();

                            break;
                        case _questionEnterName:
                            TimeoutTimer.Stop();
                            question = _questionEnterName;
                            await _bot.SendTextMessageAsync(message.Chat.Id, $"@{callbackQuery.From.Username} \n" + _questionEnterName, replyMarkup: new ForceReplyMarkup { Selective = true });

                            TimeoutTimer.Start();
                            break;
                        case "Security":
                        case "NoSecurity":
                            TimeoutTimer.Stop();
                            var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == message.Chat.Id && x.UserId == callbackQuery.From.Id);

                            if (prevQuestion == null || prevQuestion.Question != _questionSecurity)
                            {
                                Console.WriteLine(prevQuestion.Question);
                                await SendTextToUser(message.Chat.Id);
                                break;
                            }

                            var teamName = await GetTeamName(message.Chat.Id, callbackQuery.From.Id);
                            question = _questionMultipleTeams;
                            if (teamName != RepositoryTgBotJobSettings.CoreTeam)
                            {
                                inlineMessage.Text = $"@{callbackQuery.From.Username} \n" + _questionMultipleTeams;
                                inlineMessage.ReplyMarkup = new InlineKeyboardMarkup(new[]
                                {
                                new [] // first row
                                {
                                    InlineKeyboardButton.WithCallbackData("Yes", "Core"),
                                    InlineKeyboardButton.WithCallbackData("No", "NoCore")
                                }
                            });

                                //await _bot.EditMessageTextAsync(message.Chat.Id, message.MessageId, inlineMessage.Text, ParseMode.Default,
                                //        false, inlineMessage.ReplyMarkup);

                                await _bot.SendTextMessageAsync(message.Chat.Id, inlineMessage.Text, ParseMode.Default,
                                    replyMarkup: inlineMessage.ReplyMarkup);
                                TimeoutTimer.Start();
                            }
                            else
                            {
                                await CreateBotHistory(message.Chat.Id, callbackQuery.From.Id, callbackQuery.From.Username, question, callbackQuery.Data);
                                historyCreateSkipped = true;
                                callbackQuery.Data = "NoCore";
                                await SendResponseMarkup(callbackQuery, message);
                            }
                            break;
                        case "Core":
                        case "NoCore":
                            TimeoutTimer.Stop();
                            prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == message.Chat.Id && x.UserId == callbackQuery.From.Id);
                            if (prevQuestion == null || prevQuestion.Question != _questionMultipleTeams)
                            {
                                await SendTextToUser(message.Chat.Id);
                                break;
                            }
                            prevQuestion.Answer = callbackQuery.Data;
                            await _telegramBotHistoryRepository.SaveAsync(prevQuestion);

                            await SendTextToUser(message.Chat.Id, $"@{callbackQuery.From.Username} \n" + "Creating repository. Please wait...");

                            question = await CreateRepoAsync(message.Chat.Id, callbackQuery.From.Id);

                            break;
                        case _resetTeam:
                            TimeoutTimer.Stop();
                            await ClearTeam(message.Chat.Id, callbackQuery.From);
                            break;
                    }

                    if (!historyCreateSkipped)
                        await CreateBotHistory(message.Chat.Id, callbackQuery.From.Id, callbackQuery.From.Username, question, callbackQuery.Data);
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotService.SendResponseMarkup", callbackQuery.Data, ex);
                return;
            }
        }

        public void Start()
        {
            try
            {
                _log.WriteInfo("TelegramBotService.Start", "Start", "");

                var me = _bot.GetMeAsync().Result;
                Console.Title = me.Username;

                _bot.StartReceiving(Array.Empty<UpdateType>());
                _log.WriteInfo(nameof(TelegramBotService), nameof(TelegramBotService), $"Start listening for @{me.Username}");
            }
            catch (Exception ex)
            {
                _log.WriteError("TelegramBotService.Start", "", ex);
                return;
            }
        }

        public void Stop()
        {
            try
            {
                _log.WriteInfo("TelegramBotService.Stop", "Stop", "");

                _bot.StopReceiving();
                _log.WriteInfo(nameof(TelegramBotService), nameof(TelegramBotService), "Stop listening.");
            }
            catch (Exception ex)
            {
                _log.WriteError("TelegramBotService.Stop", "", ex);
                return;
            }
        }

        public void Dispose()
        {
            Dispose();
        }

        #region Private Methods

        private async Task TeamSelected(long chatId, Telegram.Bot.Types.User user, string teamId)
        {
            var data = new { chatId, user, teamId }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.Teamselected", "Team selected message", data);

                TimeoutTimer.Stop();
                var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == chatId && x.UserId == user.Id);
                if (prevQuestion == null || prevQuestion.Question == _chooseTeam)
                {
                    await _bot.SendTextMessageAsync(chatId, $"@{user.Username} \n" + _questionEnterName, replyMarkup: new ForceReplyMarkup { Selective = true });
                    await CreateBotHistory(chatId, user.Id, user.Username, _questionEnterName, teamId);
                    TimeoutTimer.Start();
                }
                else
                {
                    await SendTextToUser(chatId);
                }
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotService.TeamSelected", data, ex);
                throw;
            }
        }

        private async Task SendTextToUser(long chatId, string text = "")
        {
            const string usage = @"
Usage:
/create - to start creating repository
/resetMyTeam - to reset your team";

            await _bot.SendTextMessageAsync(
                chatId,
                String.IsNullOrWhiteSpace(text) ? usage : text,
                replyMarkup: new ReplyKeyboardRemove());
        }

        private async Task<bool> CreateBotHistory(long chatId, long userId, string telegramUserName, string question, string answer = null)
        {
            var data = new { chatId, userId, telegramUserName, question, answer }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.CreateBotHistory", "Create bot history", data);

                var entity = new TelegramBotHistory
                {
                    RowKey = Guid.NewGuid().ToString(),
                    ChatId = chatId,
                    UserId = userId,
                    TelegramUserName = telegramUserName,
                    Question = question
                };

                // first of all, we need to add answer for previous question
                if (!String.IsNullOrWhiteSpace(answer))
                {
                    var prevQuestion = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == entity.ChatId && x.UserId == entity.UserId);
                    if (prevQuestion != null)
                    {
                        prevQuestion.Answer = answer;
                        await _telegramBotHistoryRepository.SaveAsync(prevQuestion);
                    }
                }

                // add current question
                await _telegramBotHistoryRepository.SaveAsync(entity);

                return true;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.CreateBotHistory", data, ex);
                return false;
            }
        }

        private InlineMessage TeamListToSend(string username, List<Team> teamsToShow, string message = "")
        {
            var data = new { username, teamsToShow, message }.ToJson();
            try
            {
                _log.WriteInfo("TelegramBotService.TeamListToSend", "Get team list to send", data);

                var inlineMessage = new InlineMessage();
                var maxRowLength = 2;
                if (teamsToShow.Any())
                {
                    inlineMessage.Text = $"@{username} \n" + message + _chooseTeam;
                    var inlineKeyBoardButtons = new List<List<InlineKeyboardButton>>();
                    var buttons = new List<InlineKeyboardButton>();
                    foreach (var team in teamsToShow)
                    {
                        if (buttons.Count == maxRowLength)
                        {
                            inlineKeyBoardButtons.Add(buttons.Select(x => x).ToList());
                            buttons.RemoveRange(0, maxRowLength);
                        }

                        buttons.Add(InlineKeyboardButton.WithCallbackData(team.Name, team.Id.ToString()));
                    }
                    inlineKeyBoardButtons.Add(buttons);

                    var keyboardMarkup = new InlineKeyboardMarkup(inlineKeyBoardButtons);

                    inlineMessage.ReplyMarkup = keyboardMarkup;
                }
                return inlineMessage;
            }
            catch (Exception ex)
            {
                _log.WriteError("TelegramBotActions.TeamListToSend", data, ex);
                throw;
            }
        }

        private async Task<string> CreateRepoAsync(long chatId, long userId)
        {
            var data = new { chatId, userId }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.CreateRepoAsync", "Create repo async", data);

                var result = new TelegramBotActionResult();
                var repoToCreate = await GetRepoToCreate(chatId, userId);
                if (repoToCreate.MenuAction == _createGithubRepo)
                {
                    result = await _actions.CreateRepo(repoToCreate);
                    Console.WriteLine($"CreatingRepo! \n " +
                        $"repoToCreate.AddCoreTeam: {repoToCreate.AddCoreTeam} \n " +
                        $"repoToCreate.AddSecurityTeam: {repoToCreate.AddSecurityTeam} \n " +
                        $"repoToCreate.ChatId: {repoToCreate.ChatId} \n " +
                        $"repoToCreate.Description: {repoToCreate.Description} \n " +
                        $"repoToCreate.RepoName: {repoToCreate.RepoName} \n " +
                        $"repoToCreate.TeamId: {repoToCreate.TeamId} \n " +
                        $"repoToCreate.UserId: {repoToCreate.UserId} ");
                }
                else
                {
                    result = await _actions.CreateLibraryRepo(repoToCreate);
                    Console.WriteLine($"CreatingLibraryRepo! \n " +
                        $"repoToCreate.ChatId: {repoToCreate.ChatId} \n " +
                        $"repoToCreate.Description: {repoToCreate.Description} \n " +
                        $"repoToCreate.RepoName: {repoToCreate.RepoName} \n " +
                        $"repoToCreate.TeamId: {repoToCreate.TeamId} \n " +
                        $"repoToCreate.UserId: {repoToCreate.UserId} ");
                }

                await SendTextToUser(chatId, result.Message);
                return result.Message;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.CreateRepoAsync", data, ex);
                throw;
            }
        }

        private async Task<string> GetMenuAction(long chatId, long userId, string userName, string teamName)
        {
            var data = new { chatId, userId, userName, teamName }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.GetMenuAction", "Get menu action", data);

                var question = String.Empty;

                if (!String.IsNullOrEmpty(teamName))
                {
                    var addTeam = $"\nYour team is \"{teamName}\"";

                    question = $"@{userName} \n" + _mainMenu + addTeam;
                }
                else
                {
                    question = $"@{userName} \n" + _mainMenu;
                }

                var history = await _telegramBotHistoryRepository.GetLatestAsync(x => x.Question == question && x.ChatId == chatId && x.UserId == userId);
                return history?.Answer;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.GetMenuAction", data, ex);
                throw;
            }
        }

        private async Task<RepoToCreate> GetRepoToCreate(long chatId, long userId)
        {
            var data = new { chatId, userId }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.GetRepoToCreate", "Get RepoToCreate entity", data);

                var repoToCreate = new RepoToCreate
                {
                    ChatId = chatId,
                    UserId = userId
                };

                var questionsToAnswer = new[] { _chooseTeam, _questionEnterName, _questionEnterDesc, _questionSecurity, _questionMultipleTeams };
                foreach (var question in questionsToAnswer)
                {
                    var history = await _telegramBotHistoryRepository.GetLatestAsync(x => x.Question == question && x.ChatId == chatId && x.UserId == userId);
                    if (history == null) continue;

                    var answer = history.Answer;

                    switch (question)
                    {
                        case _chooseTeam:
                            repoToCreate.TeamId = Convert.ToInt32(answer);
                            break;
                        case _questionEnterName:
                            repoToCreate.RepoName = answer;
                            break;
                        case _questionEnterDesc:
                            repoToCreate.Description = answer;
                            break;
                        case _questionSecurity:
                            repoToCreate.AddSecurityTeam = answer == "Security";
                            break;
                        case _questionMultipleTeams:
                            repoToCreate.AddCoreTeam = answer == "Core";
                            break;
                    }
                }

                return repoToCreate;
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.GetRepoToCreate", data, ex);
                throw;
            }
        }

        public async Task<string> GetTeamName(long chatId, long userId)
        {
            var data = new { chatId, userId }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.GetTeamName", "Get team name", data);

                var entity = await _telegramBotHistoryRepository.GetLatestAsync(x =>
                    x.Question == _chooseTeam && x.ChatId == chatId && x.UserId == userId);
                if (entity == null)
                    return String.Empty;

                var teamId = entity.Answer;

                return await _actions.GetTeamById(Convert.ToInt32(teamId));
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.GetTeamName", data, ex);
                throw;
            }
        }

        public async Task ClearTeam(long chatId, Telegram.Bot.Types.User user)
        {
            var data = new { chatId, user }.ToJson();
            try
            {
                await _log.WriteInfoAsync("TelegramBotService.ClearTeam", "Clear team", data);

                var entities = await _telegramBotHistoryRepository.GetAllAsync(x =>
                    x.Question == _chooseTeam && x.ChatId == chatId && x.UserId == user.Id);
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        await _telegramBotHistoryRepository.RemoveAsync(entity.RowKey);
                    }
                }
                await SendTextToUser(chatId, $"@{user.Username} \n" + "Your team was reseted.");
            }
            catch (Exception ex)
            {
                await _log.WriteErrorAsync("TelegramBotActions.ClearTeam", data, ex);
                throw;
            }
        }

        private async Task<int> GetUserTeamId(long chatId, long userId)
        {
            var entity = await _telegramBotHistoryRepository.GetLatestAsync(x => x.ChatId == chatId && x.UserId == userId && x.Question == _chooseTeam);
            return entity?.Answer.ParseIntOrDefault(0) ?? 0;
        }

        private async Task<bool> CheckForGroupAccess(long chatId, long groupId)
        {
            if (RepositoryTgBotJobSettings.AllowedGroupId != 0 && groupId != RepositoryTgBotJobSettings.AllowedGroupId)
            {
                await SendTextToUser(chatId, "Access denied");
                return false;
            }

            return true;
        }

        #endregion
        public static async Task UpdateListOfTeams()
        {
            teams = await _actions.GetTeamsAsync();
            teams.Sort(new TeamByNameComparer());
        }

        public async void Timeout()
        {
            if (CurrentUser.ChatId != 0)
            {
                await CreateBotHistory(CurrentUser.ChatId, CurrentUser.User.Id, CurrentUser.User.Username, "Timeout");
                await SendTextToUser(CurrentUser.ChatId, $"@{CurrentUser.User.Username} Sorry, but time is out. Please create your repository again.");
                TimeoutTimer.Stop();
            }


        }

        private bool CheckTotalTimeLimit(DateTime dateTime)
        {
            var exactTimeNow = DateTime.Now.ToUniversalTime();
            var timeSpan = exactTimeNow.Subtract(dateTime);
            if(timeSpan.TotalMinutes > RepositoryTgBotJobSettings.TotalTimeLimitInMinutes)
            {
                return false;
            }

            return true;
        }

        class TeamByNameComparer : IComparer<Team>
        {
            public int Compare(Team a, Team b)
            {
                return a.Name.CompareTo(b.Name);
            }
        }
    }
}
