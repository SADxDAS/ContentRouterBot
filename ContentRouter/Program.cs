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
                services.AddSingleton<IChannelRepository, JsonChannelRepository>();
                services.AddHostedService<UiBotWorker>();
                services.AddHostedService<UserBotWorker>();
            })
            .Build();

        Console.WriteLine("====================================================");
        Console.WriteLine("🚀 FastRouter (Rule-based) успешно запущен");
        Console.WriteLine("====================================================");

        await host.RunAsync();
    }
}