﻿using Hangfire;
using Hangfire.Storage;
using MediatR;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Norm.Services
{
    public class NormHostedService : IHostedService
    {
        private readonly IBotService bot;
        private readonly IMediator mediator;

        public NormHostedService(IBotService bot, IMediator mediator)
        {
            this.bot = bot;
            this.mediator = mediator;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await this.mediator.Send(new Database.Requests.Database.Migrate(), cancellationToken);
            using IStorageConnection connection = JobStorage.Current.GetConnection();
            foreach (RecurringJobDto rJob in StorageConnectionExtensions.GetRecurringJobs(connection))
            {
                RecurringJob.RemoveIfExists(rJob.Id);
            }
            await this.bot.StartAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await this.bot.StopAsync();
        }
    }
}
