﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using HttpReports.Core.Config;
using HttpReports.Dashboard.Services.Quartz;
using HttpReports.Monitor;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using Quartz;
using Quartz.Impl;
using Quartz.Logging;
using Quartz.Spi;

namespace HttpReports.Dashboard.Services
{
    public class QuartzSchedulerService
    {
        private const string SchedulerGroup = "HttpReports_Scheduler";

        private const string SchedulerTag = "monitor_";

        private IHttpReportsStorage _storage;

        private IScheduler _scheduler = null;

        public IJobFactory JobFactory { get; }

        protected QuartzLogProvider QuartzLogProvider { get; }

        protected ILogger<QuartzSchedulerService> Logger { get; }

        public QuartzSchedulerService(IJobFactory jobFactory, QuartzLogProvider quartzLogProvider, ILogger<QuartzSchedulerService> logger, IHttpReportsStorage storage)
        {
            JobFactory = jobFactory;
            QuartzLogProvider = quartzLogProvider;
            Logger = logger;
            _storage = storage;
        }

        public async Task InitAsync()
        {
            _scheduler = await StdSchedulerFactory.GetDefaultScheduler().ConfigureAwait(false) ?? throw new TypeInitializationException(nameof(QuartzSchedulerService), null);
            LogProvider.SetCurrentLogProvider(QuartzLogProvider);
            _scheduler.JobFactory = JobFactory;

            await ClearDataJobAsync();

            await InitMonitorJobAsync();

            await _scheduler.Start().ConfigureAwait(false);
        }

        public async Task InitMonitorJobAsync()
        {
            List<IMonitorJob> list = await _storage.GetMonitorJobs();

            if (list == null || list.Count == 0)
            {
                return;
            }

            list = list.Where(x => x.Status == 1).ToList();

            foreach (var item in list)
            {
                await ScheduleJobAsync(item);
            }
        }

        private async Task ScheduleJobAsync(IMonitorJob model)
        {
            var job = JobBuilder.Create<MonitorBackendJob>().
                   WithIdentity(SchedulerTag + model.Id, SchedulerGroup)
                   .SetJobData(new JobDataMap { { "job", model } }).Build();

            var trigger = TriggerBuilder.Create().WithCronSchedule(model.CronLike).Build();

            await _scheduler.ScheduleJob(job, trigger);
        }

        private async Task DeleteJobAsync(IJobDetail job)
        {
            if (_scheduler.CheckExists(job.Key).Result)
            {
                await _scheduler.PauseJob(job.Key);

                await _scheduler.DeleteJob(job.Key);
            }
        }

        public async Task UpdateMonitorJobAsync()
        {
            List<IMonitorJob> list = await _storage.GetMonitorJobs();

            if (list == null || list.Count == 0)
            {
                return;
            }

            foreach (var k in list)
            {
                var job = await _scheduler.GetJobDetail(new JobKey(SchedulerTag + k.Id, SchedulerGroup));

                if (job == null)
                {
                    if (k.Status == 1) await ScheduleJobAsync(k);
                }
                else
                {
                    if (k.Status == 0)
                    {
                        await DeleteJobAsync(job);
                    }
                    else
                    {
                        IMonitorJob monitorJob = job.JobDataMap.Get("job") as IMonitorJob;

                        // 判断是否有修改，如果修改后，重置Job
                        if (JsonConvert.SerializeObject(k) != JsonConvert.SerializeObject(monitorJob))
                        {
                            await DeleteJobAsync(job);
                            await ScheduleJobAsync(k);
                        }
                    }
                }
            }
        }

        public async Task ClearDataJobAsync()
        {
            var job = JobBuilder.Create<ClearReportsDataJob>().Build();

            var trigger = TriggerBuilder.Create().WithCronSchedule(BasicConfig.ClearDataCornLike).Build();

            await _scheduler.ScheduleJob(job, trigger);
        }
    }
}