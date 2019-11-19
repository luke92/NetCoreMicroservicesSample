using Consul;
using DataModel;
using Elastic.Apm.NetCoreAll;
using Events.Infrastructure.RabbitMQ;
using Events.Users;
using FluentValidation.AspNetCore;
using MediatR;
using MessagesService.Infrastructure.Consul;
using MessagesService.Infrastructure.Filter;
using MessagesService.Infrastructure.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RawRabbit.vNext;
using RawRabbit.vNext.Pipe;
using Serilog;
using Serilog.Exceptions;
using Serilog.Sinks.Elasticsearch;
using System;
using System.Reflection;

namespace MessagesService
{
    public class Startup
    {
        private readonly IConfigurationRoot Configuration;

        public Startup(IWebHostEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", reloadOnChange: true, optional: true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();

            var elasticUri = Configuration["ElasticConfiguration:Uri"];

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticUri))
                {
                    AutoRegisterTemplate = true
                })
            .CreateLogger();
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMediatR(typeof(Startup).GetTypeInfo().Assembly);

            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

            services.AddDbContext<DatabaseContext>(options => options.UseSqlServer(Configuration.GetConnectionString(ConnectionStringKeys.App)));

            services.AddOptions();

            var consulOptions = new ConsulOptions();
            Configuration.GetSection(nameof(ConsulOptions)).Bind(consulOptions);
            services.Configure<ConsulOptions>(Configuration.GetSection(nameof(ConsulOptions)));
            services.AddSingleton<IConsulClient, ConsulClient>(p => new ConsulClient(consulConfig =>
            {
                var address = consulOptions.Address;
                consulConfig.Address = new Uri(address);
            }));

            var rabbitOptions = new RabbitOptions();
            Configuration.GetSection(nameof(RabbitOptions)).Bind(rabbitOptions);
            services.Configure<RabbitOptions>(Configuration.GetSection(nameof(RabbitOptions)));

            services.AddRawRabbit(new RawRabbitOptions
            {
                ClientConfiguration = rabbitOptions
            });

            services.AddSingleton<IRabbitEventListener, RabbitEventListener>();

            services
                .AddMvc(opt => { opt.Filters.Add(typeof(ExceptionFilter)); })
                .AddFluentValidation(cfg => { cfg.RegisterValidatorsFromAssemblyContaining<Startup>(); });


            services.AddControllers()
                .AddNewtonsoftJson();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory, IHostApplicationLifetime lifetime)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseAllElasticApm(Configuration);


            loggerFactory.AddSerilog();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            app.RegisterWithConsul(lifetime);

            app.UseRabbitSubscribe<UserCreated>();
        }
    }
}
