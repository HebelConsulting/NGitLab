﻿using System;
using System.Collections.Generic;
using NGitLab.Models;

namespace NGitLab.Mock.Clients;

internal sealed class ProjectVariableClient : ClientBase, IProjectVariableClient
{
    private readonly int _projectId;

    public ProjectVariableClient(ClientContext context, ProjectId projectId)
        : base(context)
    {
        _projectId = Server.AllProjects.FindProject(projectId.ValueAsString()).Id;
    }

    public Variable this[string key, string environmentScope = null] => throw new NotImplementedException();

    public IEnumerable<Variable> All => throw new NotImplementedException();

    public Variable Create(VariableCreate model)
    {
        throw new NotImplementedException();
    }

    public void Delete(string key) => Delete(key, null);

    public void Delete(string key, string environmentScope)
    {
        throw new NotImplementedException();
    }

    public Variable Update(string key, VariableUpdate model) => Update(key, null, model);

    public Variable Update(string key, string environmentScope, VariableUpdate model)
    {
        throw new NotImplementedException();
    }
}
