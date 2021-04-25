// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;

namespace Microsoft.BotBuilderSamples.Bots
{

    // Timers cannot be persisted (serialized). Therefore, keep timer IDs only in the conversation state
    // Timers themselves are stored in a per-instance dictionary and are ephemeral
    public class ConversationData
    {
        public int TimerID;
    }

    public class EchoBot : ActivityHandler
    {
        private readonly TimeSpan _timerDueTime = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _timerPeriod = TimeSpan.FromSeconds(5);

        // Conversation timer dictionary 
        private static ConcurrentDictionary<int, Timer> conversationTimers = new ConcurrentDictionary<int, Timer>();

        private readonly BotState _conversationState;
        private readonly IConfiguration _configuration;
        private readonly Random _random = new Random();
        public EchoBot(ConversationState conversationState, IConfiguration configuration)
        {
            _conversationState = conversationState;
            _configuration = configuration;
        }

        static SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var conversationStateAccessors = _conversationState.CreateProperty<ConversationData>(nameof(ConversationData));
            var conversationData = await conversationStateAccessors.GetAsync(turnContext, () => new ConversationData());

            string userText = turnContext.Activity.Text;
            string replyText;

            if (userText == "stop")
            {
                // Emulate connection drop. All ephemeral state wiped out
                replyText = "Connection dropped, timer reset";
                foreach (var value in conversationTimers.Values)
                {
                    value.Change(Timeout.Infinite, Timeout.Infinite);
                    await value.DisposeAsync();
                }
                conversationTimers.Clear();
            }
            else
            {
                Timer timer = conversationTimers.GetOrAdd(conversationData.TimerID, (id) =>
                {
                    // Create a new timer if it does not exist. Most likely this is a result of a reconnect
                    // This only works in Telephony and DLS channels where conversations are affinitized to instances
                    var adapter = turnContext.Adapter;
                    var conversationReference = turnContext.Activity.GetConversationReference();
                    conversationData.TimerID = id;
                    return CreateTimerForConversation(adapter, conversationReference, cancellationToken);
                });

                // Reset the timer if it exists (this call thread-safe)
                timer.Change(_timerDueTime, _timerPeriod);

                replyText = $"You said {userText}";
            }

            await turnContext.SendActivityAsync(MessageFactory.Text(replyText, replyText), cancellationToken);
            await _conversationState.SaveChangesAsync(turnContext);
        }

        protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var welcomeText = $"Hi! Say something in the next {_timerPeriod.TotalSeconds} seconds.";
            foreach (var member in membersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await turnContext.SendActivityAsync(MessageFactory.Text(welcomeText, welcomeText), cancellationToken);
                }
            }

            var adapter = turnContext.Adapter;
            var conversationReference = turnContext.Activity.GetConversationReference();

            Timer timer = CreateTimerForConversation(adapter, conversationReference, cancellationToken);
            var timerId = _random.Next();
            if (conversationTimers.TryAdd(timerId, timer) )
            {
                var conversationStateAccessors = _conversationState.CreateProperty<ConversationData>(nameof(ConversationData));
                var conversationData = await conversationStateAccessors.GetAsync(turnContext, () => new ConversationData());
                conversationData.TimerID = timerId;
                await _conversationState.SaveChangesAsync(turnContext);
            }
        }

        private Timer CreateTimerForConversation(BotAdapter adapter, ConversationReference conversationReference, CancellationToken cancellationToken)
        {
            Timer timer = new Timer(async (object _) =>
            {
                var reminderText = $"Are you still there?";
                var reminder = MessageFactory.Text(reminderText, reminderText);

                // Synchronization is necessary since the timer callback is accessing _configuration and adapter
                await semaphoreSlim.WaitAsync();

                try
                {
                    var MsAppId = _configuration["MicrosoftAppId"];

                    // If the channel is the Emulator, and authentication is not in use,
                    // the AppId will be null.  We generate a random AppId for this case only.
                    // This is not required for production, since the AppId will have a value.
                    if (string.IsNullOrEmpty(MsAppId))
                    {
                        MsAppId = Guid.NewGuid().ToString(); //if no AppId, use a random Guid
                    }

                    await adapter.ContinueConversationAsync(
                       MsAppId,
                       conversationReference,
                       (ITurnContext turnContext, CancellationToken cancellationToken) => turnContext.SendActivityAsync(reminder, cancellationToken),
                       cancellationToken);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
                finally
                {
                    semaphoreSlim.Release();
                }
            },
            null, _timerDueTime, _timerPeriod);
            return timer;
        }
    }
}
