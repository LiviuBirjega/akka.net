﻿//-----------------------------------------------------------------------
// <copyright file="Mailboxes.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Akka.Actor;
using Akka.Annotations;
using Akka.Configuration;
using Akka.Dispatch.MessageQueues;
using Akka.Event;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Dispatch
{
    /// <summary>
    /// Contains the directory of all <see cref="MailboxType"/>s registered and configured with a given <see cref="ActorSystem"/>.
    /// </summary>
    public class Mailboxes
    {
        /// <summary>
        ///     The system
        /// </summary>
        private readonly ActorSystem _system;

        private readonly DeadLetterMailbox _deadLetterMailbox;
        /// <summary>
        /// TBD
        /// </summary>
        public static readonly string DefaultMailboxId = "akka.actor.default-mailbox";
        /// <summary>
        /// TBD
        /// </summary>
        public static readonly string NoMailboxRequirement = "";
        private readonly Dictionary<Type, string> _mailboxBindings;
        private readonly Config _defaultMailboxConfig;

        private readonly ConcurrentDictionary<string, MailboxType> _mailboxTypeConfigurators = new();

        private Settings Settings => _system.Settings;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Mailboxes" /> class.
        /// </summary>
        /// <param name="system">The system.</param>
        public Mailboxes(ActorSystem system)
        {
            _system = system;
            _deadLetterMailbox = new DeadLetterMailbox(system.DeadLetters);
            var mailboxConfig = system.Settings.Config.GetConfig("akka.actor.mailbox");
            if (mailboxConfig.IsNullOrEmpty())
                throw ConfigurationException.NullOrEmptyConfig<Mailboxes>("akka.actor.mailbox");

            var requirements = mailboxConfig.GetConfig("requirements").AsEnumerable().ToList();
            _mailboxBindings = new Dictionary<Type, string>();
            foreach (var kvp in requirements)
            {
                var type = Type.GetType(kvp.Key);
                if (type == null)
                {
                    Warn($"Mailbox Requirement mapping [{kvp.Key}] is not an actual type");
                    continue;
                }
                _mailboxBindings.Add(type, kvp.Value.GetString());
            }

            _defaultMailboxConfig = Settings.Config.GetConfig(DefaultMailboxId);
            _defaultStashCapacity = StashCapacityFromConfig(Dispatchers.DefaultDispatcherId, DefaultMailboxId);
        }

        /// <summary>
        /// TBD
        /// </summary>
        public DeadLetterMailbox DeadLetterMailbox { get { return _deadLetterMailbox; } }

        /// <summary>
        /// Check if this actor class can have a required message queue type.
        /// </summary>
        /// <param name="actorType">The type to check.</param>
        /// <returns><c>true</c> if this actor has a message queue type requirement. <c>false</c> otherwise.</returns>
        public bool HasRequiredType(Type actorType)
        {
            var interfaces = actorType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var element = interfaces[i];
                if (element.IsGenericType && element.GetGenericTypeDefinition() == RequiresMessageQueueGenericType)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if this <see cref="MailboxType"/> implements the <see cref="IProducesMessageQueue{TQueue}"/> interface.
        /// </summary>
        /// <param name="mailboxType">The type of the <see cref="MailboxType"/> to check.</param>
        /// <returns><c>true</c> if this mailboxtype produces queues. <c>false</c> otherwise.</returns>
        public bool ProducesMessageQueue(Type mailboxType)
        {
            var interfaces = mailboxType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var element = interfaces[i];
                if (element.IsGenericType && element.GetGenericTypeDefinition() == ProducesMessageQueueGenericType)
                {
                    return true;
                }
            }

            return false;
        }

        private string LookupId(Type queueType)
        {
            if (!_mailboxBindings.TryGetValue(queueType, out string id))
                throw new ConfigurationException($"Mailbox Mapping for [{queueType}] not configured");
            return id;
        }

        /// <summary>
        /// Returns a <see cref="MailboxType"/> as specified in configuration, based on the type, or if not defined null.
        /// </summary>
        /// <param name="queueType">The mailbox we need given the queue requirements.</param>
        /// <exception cref="ConfigurationException">This exception is thrown if a mapping is not configured for the given <paramref name="queueType"/>.</exception>
        /// <returns>A <see cref="MailboxType"/> as specified in configuration, based on the type, or if not defined null.</returns>
        public MailboxType LookupByQueueType(Type queueType)
        {
            return Lookup(LookupId(queueType));
        }

        /// <summary>
        /// Returns a <see cref="MailboxType"/> as specified in configuration, based on the id, or if not defined null.
        /// </summary>
        /// <param name="id">The ID of the mailbox to lookup</param>
        /// <exception cref="ConfigurationException">
        /// This exception is thrown if the mailbox type is not configured or the system could not load or find the type specified.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// This exception is thrown if the mailbox type could not be instantiated.
        /// </exception>
        /// <returns>The <see cref="MailboxType"/> specified in configuration or if not defined null.</returns>
        public MailboxType Lookup(string id) => LookupConfigurator(id);

        // don't care if these happen twice
        private bool _mailboxSizeWarningIssued = false;
        private bool _mailboxNonZeroPushTimeoutWarningIssued = false;

        private MailboxType LookupConfigurator(string id)
        {
            if (!_mailboxTypeConfigurators.TryGetValue(id, out var configurator))
            {
                // It doesn't matter if we create a mailbox type configurator that isn't used due to concurrent lookup.
                if (id.Equals("unbounded"))
                    configurator = new UnboundedMailbox();
                else if (id.Equals("bounded"))
                    configurator = new BoundedMailbox(Settings, Config(id));
                else
                {
                    if (!Settings.Config.HasPath(id)) throw new ConfigurationException($"Mailbox Type [{id}] not configured");
                    var conf = Config(id);

                    var mailboxTypeName = conf.GetString("mailbox-type", null);
                    if (string.IsNullOrEmpty(mailboxTypeName))
                        throw new ConfigurationException($"The setting mailbox-type defined in [{id}] is empty");
                    var mailboxType = Type.GetType(mailboxTypeName) 
                        ?? throw new ConfigurationException($"Found mailbox-type [{mailboxTypeName}] in configuration for [{id}], but could not find that type in any loaded assemblies.");
                    var args = new object[] { Settings, conf };
                    try
                    {
                        configurator = (MailboxType)Activator.CreateInstance(mailboxType, args);

                        if (!_mailboxNonZeroPushTimeoutWarningIssued)
                        {
                            if (configurator is IProducesPushTimeoutSemanticsMailbox m && m.PushTimeout.Ticks > 0L)
                            {
                                Warn($"Configured potentially-blocking mailbox [{id}] configured with non-zero PushTimeOut ({m.PushTimeout}), " +
                                    "which can lead to blocking behavior when sending messages to this mailbox. " +
                                    $"Avoid this by setting `{id}.mailbox-push-timeout-time` to `0`.");

                                _mailboxNonZeroPushTimeoutWarningIssued = true;
                            }

                            // good; nothing to see here, move along, sir.
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException($"Cannot instantiate MailboxType {mailboxType}, defined in [{id}]. Make sure it has a public " +
                                                     "constructor with [Akka.Actor.Settings, Akka.Configuration.Config] parameters", ex);
                    }
                }

                // add the new configurator to the mapping, or keep the existing if it was already added
                _mailboxTypeConfigurators.AddOrUpdate(id, configurator, (_, type) => type);
            }

            return configurator;
        }

        /// <summary>
        /// INTERNAL API
        /// </summary>
        /// <param name="id">The id of the mailbox whose config we're going to generate.</param>
        /// <returns>A <see cref="Config"/> object for the mailbox with <paramref name="id"/></returns>
        private Config Config(string id)
        {
            return ConfigurationFactory.ParseString($"id:{id}")
                .WithFallback(Settings.Config.GetConfig(id))
                .WithFallback(_defaultMailboxConfig);
        }

        private static readonly Type RequiresMessageQueueGenericType = typeof (IRequiresMessageQueue<>);

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="actorType">TBD</param>
        /// <returns>TBD</returns>
        public Type GetRequiredType(Type actorType)
        {
            var interfaces = actorType.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var element = interfaces[i];
                if (element.IsGenericType && element.GetGenericTypeDefinition() == RequiresMessageQueueGenericType)
                {
                    return element.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static readonly Type ProducesMessageQueueGenericType = typeof (IProducesMessageQueue<>);
        private Type GetProducedMessageQueueType(MailboxType mailboxType)
        {
            var interfaces = mailboxType.GetType().GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var element = interfaces[i];
                if (element.IsGenericType && element.GetGenericTypeDefinition() == ProducesMessageQueueGenericType)
                {
                    return element.GetGenericArguments()[0];
                }
            }

            throw new ArgumentException(nameof(mailboxType), $"No IProducesMessageQueue<TQueue> supplied for {mailboxType}; illegal mailbox type definition.");
        }

        private Type GetMailboxRequirement(Config config)
        {
            var mailboxRequirement = config.GetString("mailbox-requirement", null);
            return mailboxRequirement == null || mailboxRequirement.Equals(NoMailboxRequirement) ? typeof (IMessageQueue) : Type.GetType(mailboxRequirement, true);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="props">TBD</param>
        /// <param name="dispatcherConfig">TBD</param>
        /// <exception cref="ArgumentException">
        /// This exception is thrown if the 'mailbox-requirement' in the given <paramref name="dispatcherConfig"/> isn't met.
        /// </exception>
        /// <returns>TBD</returns>
        public MailboxType GetMailboxType(Props props, Config dispatcherConfig)
        {
            if (dispatcherConfig == null)
                dispatcherConfig = ConfigurationFactory.Empty;
            var id = dispatcherConfig.GetString("id", null);
            var deploy = props.Deploy;
            var actorType = props.Type;
            var actorRequirement = new Lazy<Type>(() => GetRequiredType(actorType));

            var mailboxRequirement = GetMailboxRequirement(dispatcherConfig);
            var hasMailboxRequirement = mailboxRequirement != typeof(IMessageQueue);

            var hasMailboxType = dispatcherConfig.HasPath("mailbox-type") &&
                                 dispatcherConfig.GetString("mailbox-type", null) != Deploy.NoMailboxGiven;

            if (!hasMailboxType && !_mailboxSizeWarningIssued && dispatcherConfig.HasPath("mailbox-size"))
            {
                Warn($"Ignoring setting 'mailbox-size for dispatcher [{id}], you need to specify 'mailbox-type=bounded`");
                _mailboxSizeWarningIssued = true;
            }

            MailboxType VerifyRequirements(MailboxType mailboxType)
            {
                var mqType = new Lazy<Type>(() => GetProducedMessageQueueType(mailboxType));
                if (hasMailboxRequirement && !mailboxRequirement.IsAssignableFrom(mqType.Value))
                    throw new ArgumentException($"produced message queue type [{mqType.Value}] does not fulfill requirement for dispatcher [{id}]." + $"Must be a subclass of [{mailboxRequirement}]");
                if (HasRequiredType(actorType) && !actorRequirement.Value.IsAssignableFrom(mqType.Value))
                    throw new ArgumentException($"produced message queue type of [{mqType.Value}] does not fulfill requirement for actor class [{actorType}]." + $"Must be a subclass of [{actorRequirement.Value}]");
                return mailboxType;
            }

            if (!deploy.Mailbox.Equals(Deploy.NoMailboxGiven))
                return VerifyRequirements(Lookup(deploy.Mailbox));
            if (!deploy.Dispatcher.Equals(Deploy.NoDispatcherGiven) && hasMailboxType)
                return VerifyRequirements(Lookup(dispatcherConfig.GetString("id", null)));
            if (actorRequirement.Value != null)
            {
                try
                {
                    return VerifyRequirements(LookupByQueueType(actorRequirement.Value));
                }
                catch (Exception)
                {
                    if (hasMailboxRequirement)
                        return VerifyRequirements(LookupByQueueType(mailboxRequirement));
                    throw;
                }
            }
            if (hasMailboxRequirement)
                return VerifyRequirements(LookupByQueueType(mailboxRequirement));
            return VerifyRequirements(Lookup(DefaultMailboxId));
        }

        private void Warn(string msg) =>
            _system.EventStream.Publish(new Warning("mailboxes", GetType(), msg));

        private readonly AtomicReference<ImmutableDictionary<string, int>> _stashCapacityCache =
            new(ImmutableDictionary<string, int>.Empty);

        private readonly int _defaultStashCapacity;

        /// <summary>
        /// INTERNAL API
        /// <para>
        /// The capacity of the stash. Configured in the actor's mailbox or dispatcher config.
        /// </para>
        /// </summary>
        [InternalApi]
        public int StashCapacity(string dispatcher, string mailbox)
        {
            bool UpdateCache(ImmutableDictionary<string, int> cache, string key, int value)
            {
                return _stashCapacityCache.CompareAndSet(cache, cache.SetItem(key, value)) ||
                    UpdateCache(_stashCapacityCache.Value, key, value); // recursive, try again
            }

            if (dispatcher == Dispatchers.DefaultDispatcherId && mailbox == DefaultMailboxId)
                return _defaultStashCapacity;

            var cache = _stashCapacityCache.Value;
            var key = $"{dispatcher}-{mailbox}";

            if (!cache.TryGetValue(key, out var value))
            {
                value = StashCapacityFromConfig(dispatcher, mailbox);
                UpdateCache(cache, key, value);
            }

            return value;
        }

        private int StashCapacityFromConfig(string dispatcher, string mailbox)
        {
            var disp = Dispatchers.GetConfig(Settings.Config, dispatcher);
            var fallback = disp.WithFallback(Settings.Config.GetConfig(DefaultMailboxId));
            var config = mailbox == DefaultMailboxId
                ? fallback
                : Settings.Config.GetConfig(mailbox).WithFallback(fallback);
            return config.GetInt("stash-capacity");
        }
    }
}

