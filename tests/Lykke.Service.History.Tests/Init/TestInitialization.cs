﻿using System;
using System.Collections.Generic;
using Autofac;
using AutoMapper;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Cqrs;
using Lykke.Cqrs.Configuration;
using Lykke.Logs;
using Lykke.Logs.Loggers.LykkeConsole;
using Lykke.Messaging;
using Lykke.Messaging.Contract;
using Lykke.Service.History.AutoMapper;
using Lykke.Service.History.Core.Domain.History;
using Lykke.Service.History.Core.Domain.Orders;
using Lykke.Service.History.Modules;
using Lykke.Service.History.PostgresRepositories.Mappings;
using Lykke.Service.History.Settings;
using Lykke.Service.History.Workflow.Projections;
using Lykke.Service.PostProcessing.Contracts.Cqrs;
using Lykke.Service.PostProcessing.Contracts.Cqrs.Events;
using Lykke.SettingsReader.ReloadingManager;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lykke.Service.History.Tests.Init
{
    [CollectionDefinition("history-tests")]
    public class TestInitializationCollection : ICollectionFixture<TestInitialization>
    {
    }

    public class TestInitialization
    {
        static TestInitialization()
        {
            Mapper.Initialize(cfg =>
            {
                cfg.AddProfiles(typeof(ServiceProfile));
                cfg.AddProfiles(typeof(RepositoryProfile));
            });
        }

        public IContainer Container { get; }

        public TestInitialization()
        {
            Container = CreateContainer();
        }

        public IContainer CreateContainer()
        {
            var builder = new ContainerBuilder();

            builder.RegisterInstance(LogFactory.Create().AddUnbufferedConsole()).As<ILogFactory>().SingleInstance();

            builder.Register(context => new AutofacDependencyResolver(context)).As<IDependencyResolver>().SingleInstance();

            builder.RegisterType<CashInProjection>();
            builder.RegisterType<CashOutProjection>();
            builder.RegisterType<CashTransferProjection>();
            builder.RegisterType<ExecutionProjection>();

            builder.Register(ctx =>
                {
                    var logFactory = ctx.Resolve<ILogFactory>();
                    var messagingEngine = new MessagingEngine(
                        logFactory,
                        new TransportResolver(new Dictionary<string, TransportInfo>
                        {
                            {"InMemory", new TransportInfo("none", "none", "none", null, "InMemory")}
                        }));
                    return CreateEngine(ctx, messagingEngine, logFactory);
                })
                .As<ICqrsEngine>()
                .AutoActivate()
                .SingleInstance();

            RegisterRepo(builder);

            return builder.Build();
        }

        private void RegisterRepo(ContainerBuilder builder)
        {
            var connection = Environment.GetEnvironmentVariable("PostgresConnection");

            if (string.IsNullOrWhiteSpace(connection))
            {
                builder.RegisterType<InMemoryHistoryRepository>()
                    .As<IHistoryRecordsRepository>()
                    .SingleInstance();

                builder.RegisterType<InMemoryOrdersRepository>()
                    .As<IOrdersRepository>()
                    .SingleInstance();
            }
            else
            {
                var settings = new AppSettings()
                {
                    HistoryService = new HistorySettings
                    {
                        Db = new DbSettings
                        {
                            PostgresDataConnString = connection
                        }
                    }
                };

                builder.RegisterModule(new DbModule(ConstantReloadingManager.From(settings)));
            }
        }

        private CqrsEngine CreateEngine(
            IComponentContext ctx,
            IMessagingEngine messagingEngine,
            ILogFactory logFactory)
        {
            const string defaultRoute = "self";

            return new CqrsEngine(
                logFactory,
                ctx.Resolve<IDependencyResolver>(),
                messagingEngine,
                new DefaultEndpointProvider(),
                true,
                Register.DefaultEndpointResolver(new InMemoryEndpointResolver()),

                Register.BoundedContext(PostProcessingBoundedContext.Name)
                    
                     
                    .PublishingEvents(typeof(CashInProcessedEvent))
                    .With(defaultRoute)
                    .WithLoopback()
                    .WithProjection(typeof(CashInProjection), PostProcessingBoundedContext.Name)

                    .PublishingEvents(typeof(CashOutProcessedEvent))
                    .With(defaultRoute)
                    .WithLoopback()
                    .WithProjection(typeof(CashOutProjection), PostProcessingBoundedContext.Name)

                    .PublishingEvents(typeof(CashTransferProcessedEvent))
                    .With(defaultRoute)
                    .WithLoopback()
                    .WithProjection(typeof(CashTransferProjection), PostProcessingBoundedContext.Name)

                    .PublishingEvents(typeof(ExecutionProcessedEvent))
                    .With(defaultRoute)
                    .WithLoopback()
                    .WithProjection(typeof(ExecutionProjection), PostProcessingBoundedContext.Name)
                    );
        }
    }
}
