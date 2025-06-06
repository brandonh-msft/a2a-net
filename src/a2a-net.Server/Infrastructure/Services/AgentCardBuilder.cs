﻿// Copyright � 2025-Present the a2a-net Authors
//
// Licensed under the Apache License, Version 2.0 (the "License"),
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace A2A.Server.Infrastructure.Services;

/// <summary>
/// Represents the default implementation of the <see cref="IAgentCardBuilder"/> interface
/// </summary>
public class AgentCardBuilder
    : IAgentCardBuilder
{

    /// <summary>
    /// Gets the card to build and configure
    /// </summary>
    protected AgentCard Card { get; } = new();

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Card.Name = name;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithDescription(string description)
    {
        Card.Description = description;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Card.Version = version;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        Card.Url = url;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithDocumentationUrl(Uri url)
    {
        Card.DocumentationUrl = url;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithProvider(AgentProvider provider)
    {
        Card.Provider = provider;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithProvider(Action<IAgentProviderBuilder> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var builder = new AgentProviderBuilder();
        setup(builder);
        Card.Provider = builder.Build();
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder SupportsPushNotifications()
    {
        Card.Capabilities ??= new();
        Card.Capabilities.PushNotifications = true;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder SupportsStateTransitionHistory()
    {
        Card.Capabilities ??= new();
        Card.Capabilities.StateTransitionHistory = true;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder SupportsStreaming()
    {
        Card.Capabilities ??= new();
        Card.Capabilities.Streaming = true;
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithDefaultInputMode(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Card.DefaultInputModes ??= [];
        if(!Card.DefaultInputModes.Contains(contentType)) Card.DefaultInputModes.Add(contentType);
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithDefaultOutputMode(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Card.DefaultOutputModes ??= [];
        if (!Card.DefaultOutputModes.Contains(contentType)) Card.DefaultOutputModes.Add(contentType);
        return this;
    }

    /// <inheritdoc/>
    public virtual IAgentCardBuilder WithSkill(Action<IAgentSkillBuilder> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var builder = new AgentSkillBuilder();
        setup(builder);
        Card.Skills ??= [];
        Card.Skills.Add(builder.Build());
        return this;
    }

    /// <inheritdoc/>
    public virtual AgentCard Build() => Card;

}
