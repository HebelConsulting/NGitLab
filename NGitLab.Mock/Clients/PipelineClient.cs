﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using NGitLab.Models;

namespace NGitLab.Mock.Clients
{
    internal sealed class PipelineClient : ClientBase, IPipelineClient
    {
        private readonly int _projectId;
        private readonly IJobClient _jobClient;

        public PipelineClient(ClientContext context, IJobClient jobClient, int projectId)
            : base(context)
        {
            _jobClient = jobClient;
            _projectId = projectId;
        }

        public Models.Pipeline this[int id]
        {
            get
            {
                var project = GetProject(_projectId, ProjectPermission.View);
                var pipeline = project.Pipelines.GetById(id);
                if (pipeline == null)
                    throw new GitLabNotFoundException();

                return pipeline.ToPipelineClient();
            }
        }

        public IEnumerable<PipelineBasic> All
        {
            get
            {
                var project = GetProject(_projectId, ProjectPermission.View);
                return project.Pipelines.Select(p => p.ToPipelineBasicClient());
            }
        }

        public IEnumerable<Models.Job> AllJobs => _jobClient.GetJobs(JobScopeMask.All);

        public Models.Pipeline Create(string @ref)
        {
            var project = GetProject(_projectId, ProjectPermission.View);
            var pipeline = project.Pipelines.Add(@ref, JobStatus.Running, Context.User);
            return pipeline.ToPipelineClient();
        }

        public Models.Pipeline CreatePipelineWithTrigger(string token, string @ref, Dictionary<string, string> variables)
        {
            throw new NotImplementedException();
        }

        public void Delete(int pipelineId)
        {
            var project = GetProject(_projectId, ProjectPermission.View);
            var pipeline = project.Pipelines.GetById(pipelineId);
            project.Pipelines.Remove(pipeline);
        }

        public IEnumerable<PipelineVariable> GetVariables(int pipelineId)
        {
            throw new NotImplementedException();
        }

        public Models.Job[] GetJobs(int pipelineId)
        {
            return _jobClient.GetJobs(JobScopeMask.All).Where(p => p.Pipeline.Id == pipelineId).ToArray();
        }

        [Obsolete("Use JobClient.GetJobs() instead")]
        public IEnumerable<Models.Job> GetJobsInProject(JobScope scope)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<PipelineBasic> Search(PipelineQuery query)
        {
            var project = GetProject(_projectId, ProjectPermission.View);
            IEnumerable<Pipeline> pipelines = project.Pipelines;
            if (query.Sha != null)
            {
                var sha = new Sha1(query.Sha);
                pipelines = pipelines.Where(pipeline => pipeline.Sha.Equals(sha));
            }

            if (query.Name != null)
            {
                pipelines = pipelines.Where(pipeline => string.Equals(pipeline.User.Name, query.Name, StringComparison.Ordinal));
            }

            if (query.Ref != null)
            {
                pipelines = pipelines.Where(pipeline => string.Equals(pipeline.Ref, query.Ref, StringComparison.Ordinal));
            }

            if (query.Scope.HasValue)
            {
                throw new NotImplementedException();
            }

            if (query.Status.HasValue)
            {
                pipelines = pipelines.Where(pipeline => pipeline.Status == query.Status);
            }

            if (query.Username != null)
            {
                pipelines = pipelines.Where(pipeline => string.Equals(pipeline.User.UserName, query.Username, StringComparison.Ordinal));
            }

            if (query.YamlErrors.HasValue)
            {
                pipelines = pipelines.Where(pipeline => !string.IsNullOrEmpty(pipeline.YamlError));
            }

            if (query.OrderBy.HasValue)
            {
                pipelines = query.OrderBy.Value switch
                {
                    PipelineOrderBy.id => QuerySort(pipelines, query.Sort, p => p.Id),
                    PipelineOrderBy.status => QuerySort(pipelines, query.Sort, p => p.Status),
                    PipelineOrderBy.@ref => QuerySort(pipelines, query.Sort, p => p.Ref),
                    PipelineOrderBy.user_id => QuerySort(pipelines, query.Sort, p => p.User.Id),
                    PipelineOrderBy.updated_at => QuerySort(pipelines, query.Sort, p => p.UpdatedAt),
                    _ => throw new NotImplementedException(),
                };
            }
            else
            {
                pipelines = QuerySort(pipelines, query.Sort, p => p.UpdatedAt);
            }

            return pipelines.Select(pipeline => pipeline.ToPipelineBasicClient());
        }

        private IEnumerable<Pipeline> QuerySort<T>(IEnumerable<Pipeline> pipelines, PipelineSort? sort, Func<Pipeline, T> expression)
        {
            if (!sort.HasValue)
                sort = PipelineSort.desc;

            if (sort.Value == PipelineSort.desc)
                return pipelines.OrderByDescending(expression);

            return pipelines.OrderBy(expression);
        }
    }
}