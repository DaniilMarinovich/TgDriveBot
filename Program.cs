using System.Threading.Tasks;
using System.Threading;
using System;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using System.Collections.Generic;
using Telegram.Bot.Types.ReplyMarkups;
using System.Linq;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;
using System.Collections.Concurrent;

class Program
{
    private static ITelegramBotClient _botClient;
    private static ReceiverOptions _receiverOptions;
    private static DriveService _driveService;

    private static ConcurrentDictionary<long, int> _chatPageDict = new ConcurrentDictionary<long, int>();

    static async Task Main()
    {
        _botClient = new TelegramBotClient("your token");
        _receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
            ThrowPendingUpdates = true,
        };

        _driveService = GoogleDriveService.Authenticate();

        using var cts = new CancellationTokenSource();
        _botClient.StartReceiving(UpdateHandler, ErrorHandler, _receiverOptions, cts.Token);

        var me = await _botClient.GetMeAsync();
        Console.WriteLine($"{me.FirstName} запущен!");

        await Task.Delay(-1);
    }

    private static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                    {
                        var message = update.Message;
                        var chat = message.Chat;

                        if (message.Text == "/start")
                        {
                            _chatPageDict[chat.Id] = 0; 
                            await SendFileList(chat.Id);
                            return;
                        }
                        break;
                    }
                case UpdateType.CallbackQuery:
                    {
                        var callbackQuery = update.CallbackQuery;
                        var chat = callbackQuery.Message.Chat;

                        if (callbackQuery.Data.StartsWith("file_"))
                        {
                            string fileId = callbackQuery.Data.Substring(5);
                            string outputDirectory = "D:\\Downloads\\Test";

                            if (!Directory.Exists(outputDirectory))
                            {
                                Directory.CreateDirectory(outputDirectory);
                            }

                            string filePath = await GoogleDriveService.DownloadFile(_driveService, fileId, outputDirectory);
                            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            {
                                await botClient.SendDocumentAsync(chat.Id, new Telegram.Bot.Types.InputFiles.InputOnlineFile(fileStream, Path.GetFileName(filePath)));
                            }
                        }
                        else if (callbackQuery.Data == "prev_page")
                        {
                            if (_chatPageDict[chat.Id] > 0)
                            {
                                _chatPageDict[chat.Id]--;
                                await SendFileList(chat.Id);
                            }
                        }
                        else if (callbackQuery.Data == "next_page")
                        {
                            _chatPageDict[chat.Id]++;
                            await SendFileList(chat.Id);
                        }

                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }

    private static Task ErrorHandler(ITelegramBotClient botClient, Exception error, CancellationToken cancellationToken)
    {
        var ErrorMessage = error switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => error.ToString()
        };

        Console.WriteLine(ErrorMessage);
        return Task.CompletedTask;
    }

    private static async Task SendFileList(long chatId)
    {
        var files = GoogleDriveService.ListFiles(_driveService);
        int currentPage = _chatPageDict[chatId];
        int pageSize = 10;

        var paginatedFiles = files.Skip(currentPage * pageSize).Take(pageSize).ToList();
        var buttonList = new List<InlineKeyboardButton[]>();

        foreach (var file in paginatedFiles)
        {
            buttonList.Add(new InlineKeyboardButton[] { InlineKeyboardButton.WithCallbackData(file.Name, $"file_{file.Id}") });
        }

        
        var navigationButtons = new List<InlineKeyboardButton>();
        if (currentPage > 0)
        {
            navigationButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", "prev_page"));
        }
        if (files.Count > (currentPage + 1) * pageSize)
        {
            navigationButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ➡️", "next_page"));
        }
        if (navigationButtons.Count > 0)
        {
            buttonList.Add(navigationButtons.ToArray());
        }

        var inlineKeyboard = new InlineKeyboardMarkup(buttonList);
        await _botClient.SendTextMessageAsync(chatId, "Выберите файл:", replyMarkup: inlineKeyboard);
    }

    public static class GoogleDriveService
    {
        static string[] Scopes = { DriveService.Scope.Drive };
        static string ApplicationName = "Drive API .NET Quickstart";

        static string googleClientId = "your id";
        static string googleClientSecret = "your secret";

        public static DriveService Authenticate()
        {
            UserCredential credential;

            var clientSecrets = new ClientSecrets
            {
                ClientId = googleClientId,
                ClientSecret = googleClientSecret
            };

            credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets,
                Scopes,
                "user",
                CancellationToken.None,
                new FileDataStore("token.json", true)).Result;

            Console.WriteLine("Credential obtained.");

            return new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
        }

        public static IList<Google.Apis.Drive.v3.Data.File> ListFiles(DriveService service)
        {
            var request = service.Files.List();
            request.Fields = "nextPageToken, files(id, name)";
            var result = request.Execute();
            var files = result.Files;
            return files ?? new List<Google.Apis.Drive.v3.Data.File>();
        }

        public static async Task<string> DownloadFile(DriveService service, string fileId, string outputDirectory)
        {
            var fileRequest = service.Files.Get(fileId);
            fileRequest.Fields = "id, name";
            var file = fileRequest.Execute();
            string fileName = file.Name;

            string outputPath = Path.Combine(outputDirectory, fileName);

            using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                await fileRequest.DownloadAsync(fileStream);
            }

            return outputPath;
        }
    }
}
