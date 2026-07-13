// Program.cs
using System;
using System.Threading.Tasks;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

class Program
{
    static async Task Main(string[] args)
    {
        Env.Load();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Регистрация инфраструктурного слоя (JSON репозиторий)
                services.AddSingleton<IChannelRepository, JsonChannelRepository>();

                // Регистрация сервиса нейросети (загрузка весов Llama один раз)
                services.AddSingleton<IAiClassifier>(provider =>
                    new LlamaClassifier("Models/Meta-Llama-3-8B-Instruct.Q4_K_M.gguf"));

                // Регистрация фоновых процессов
                services.AddHostedService<UiBotWorker>();
                services.AddHostedService<UserBotWorker>();
            })
            .Build();

        Console.WriteLine("====================================================");
        Console.WriteLine("🚀 Модульный маршрутизатор ContentRouter успешно запущен");
        Console.WriteLine("====================================================");

        await host.RunAsync();
    }
}