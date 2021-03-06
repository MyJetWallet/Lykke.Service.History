﻿using System;
using AutoMapper;
using JetBrains.Annotations;
using Lykke.Job.History.AutoMapper;
using Lykke.Sdk;
using Lykke.Service.History.Core.Settings;
using Lykke.Service.History.PostgresRepositories.Mappings;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Lykke.Job.History
{
    [UsedImplicitly]
    public class Startup
    {
        private readonly LykkeSwaggerOptions _swaggerOptions = new LykkeSwaggerOptions
        {
            ApiTitle = "History Job",
            ApiVersion = "v1"
        };

        [UsedImplicitly]
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            Mapper.Initialize(cfg =>
            {
                cfg.AddProfiles(typeof(ServiceProfile));
                cfg.AddProfiles(typeof(RepositoryProfile));
            });

            return services.BuildServiceProvider<AppSettings>(options =>
            {
                options.SwaggerOptions = _swaggerOptions;

                options.Logs = logs =>
                {
                    logs.AzureTableName = "HistoryJobLog";
                    logs.AzureTableConnectionStringResolver = settings => settings.HistoryService.Db.LogsConnString;

                    // TODO: You could add extended logging configuration here:
                    /*
                    logs.Extended = extendedLogs =>
                    {
                        // For example, you could add additional slack channel like this:
                        extendedLogs.AddAdditionalSlackChannel("HistoryRecord", channelOptions =>
                        {
                            channelOptions.MinLogLevel = LogLevel.Information;
                        });
                    };
                    */
                };

                // TODO: Extend the service configuration
                /*
                options.Extend = (sc, settings) =>
                {
                    sc
                        .AddOptions()
                        .AddAuthentication(MyAuthOptions.AuthenticationScheme)
                        .AddScheme<MyAuthOptions, KeyAuthHandler>(MyAuthOptions.AuthenticationScheme, null);
                };
                */

                // TODO: You could add extended Swagger configuration here:
                /*
                options.Swagger = swagger =>
                {
                    swagger.IgnoreObsoleteActions();
                };
                */
            });
        }

        [UsedImplicitly]
        public void Configure(IApplicationBuilder app)
        {
            app.UseLykkeConfiguration(options =>
            {
                options.SwaggerOptions = _swaggerOptions;

                // TODO: Configure additional middleware for eg authentication or maintenancemode checks
                /*
                options.WithMiddleware = x =>
                {
                    x.UseMaintenanceMode<AppSettings>(settings => new MaintenanceMode
                    {
                        Enabled = settings.MaintenanceMode?.Enabled ?? false,
                        Reason = settings.MaintenanceMode?.Reason
                    });
                    x.UseAuthentication();
                };
                */
            });
        }
    }
}
