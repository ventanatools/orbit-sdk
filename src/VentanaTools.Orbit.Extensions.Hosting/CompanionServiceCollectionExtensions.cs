// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VentanaTools.Orbit.Extensions.Hosting;

/// <summary>
/// Runs a companion in the .NET Generic Host (contract §9.5): its handler comes from dependency
/// injection, and the companion runs as a hosted service with the same file discovery, status,
/// file watching and exit codes as <see cref="CompanionApp.RunAsync"/>. It logs through
/// <see cref="ILogger"/> instead of writing status lines, and stops when the host stops.
/// </summary>
/// <remarks>
/// <para>
/// A process runs one companion, for one manifest. The host owns Ctrl+C and shutdown: when it
/// stops, the companion's connection closes, its sessions end and their handlers' tokens are
/// cancelled, as when <see cref="CompanionApp.RunAsync"/> is cancelled.
/// </para>
/// <para>
/// Log lines follow the SDK's logging rule: states, reason codes, fixes and help links, never
/// pairing contents, paths, pipe names, setting values or face text. A handler's own exception is
/// logged with its fault, and the host's fixed error text only with <c>--verbose</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var builder = Host.CreateApplicationBuilder(args);
/// builder.Services.AddSingleton(TimeProvider.System);
/// builder.Services.AddCompanion&lt;TimeWidget&gt;();
/// await builder.Build().RunAsync();
/// </code>
/// </example>
public static class CompanionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> as a singleton, unless it is registered already,
    /// and runs the companion with it as a hosted service, with the default options.
    /// </summary>
    /// <typeparam name="THandler">The contribution handler; its constructor's parameters come from the container.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IContributionHandler =>
        AddCompanion<THandler>(services, static _ => { });

    /// <summary>
    /// Registers <typeparamref name="THandler"/> as a singleton, unless it is registered already,
    /// and runs the companion with it as a hosted service.
    /// </summary>
    /// <typeparam name="THandler">The contribution handler; its constructor's parameters come from the container.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">A <see cref="CompanionServiceOptions.Client"/> option is out of range.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services, Action<CompanionServiceOptions> configure)
        where THandler : class, IContributionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = Configure(services, configure);
        services.TryAddSingleton<THandler>();
        return Register(services, options, static provider => provider.GetRequiredService<THandler>());
    }

    /// <summary>Runs the companion as a hosted service with the handler <paramref name="handlerFactory"/> creates, with the default options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="handlerFactory">Creates the handler from the application's services, once, when the host starts.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="handlerFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services, Func<IServiceProvider, IContributionHandler> handlerFactory) =>
        AddCompanion(services, handlerFactory, static _ => { });

    /// <summary>Runs the companion as a hosted service with the handler <paramref name="handlerFactory"/> creates.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="handlerFactory">Creates the handler from the application's services, once, when the host starts.</param>
    /// <param name="configure">Sets the options.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="handlerFactory"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">A <see cref="CompanionServiceOptions.Client"/> option is out of range.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services, Func<IServiceProvider, IContributionHandler> handlerFactory,
        Action<CompanionServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(configure);
        var options = Configure(services, configure);
        return Register(services, options, handlerFactory);
    }

    private static CompanionServiceOptions Configure(IServiceCollection services, Action<CompanionServiceOptions> configure)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CompanionRegistration)))
        {
            throw new InvalidOperationException("A companion is registered already: a process runs one companion, for one manifest.");
        }

        var options = new CompanionServiceOptions();
        configure(options);
        options.Client?.Validate();
        return options;
    }

    private static IServiceCollection Register(IServiceCollection services, CompanionServiceOptions options,
        Func<IServiceProvider, IContributionHandler> handlerFactory)
    {
        var registration = new CompanionRegistration(options);
        services.AddSingleton(registration);
        services.AddHostedService(provider => new CompanionService(
            handlerFactory(provider) ?? throw new InvalidOperationException("The handler factory returned null."),
            registration.Options,
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
            provider.GetService<IHostApplicationLifetime>()));
        return services;
    }
}

/// <summary>Marks a service collection that has a companion, and keeps its options.</summary>
internal sealed class CompanionRegistration
{
    public CompanionRegistration(CompanionServiceOptions options) => Options = options;

    public CompanionServiceOptions Options { get; }
}
