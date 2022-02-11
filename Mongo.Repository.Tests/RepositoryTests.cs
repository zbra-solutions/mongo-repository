using Mongo.Repository.Impl;
using FluentAssertions;
using Google.Cloud.Mongo.V1;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZBRA.Commons;
using static Google.Cloud.Mongo.V1.PropertyOrder.Types;

namespace Mongo.Repository.Tests
{
    [Collection("DatastoreCollection")]
    public class RepositoryTests
    {
        private DatastoreFixture fixture;

        public RepositoryTests(DatastoreFixture fixture)
        {
            this.fixture = fixture;
            fixture.ClearData();
        }

        [Fact]
        public void TestMappings()
        {
            var mappings = new Mappings();
            mappings.Entity<RepoObj>().Infer(true).Build();
            mappings.Entity<OtherObj>().Infer(true).Build();

            var myRepo = new Repository<RepoObj>(fixture.GetDb(), mappings);
            var otherRepo = new Repository<OtherObj>(fixture.GetDb(), mappings);

            var obj = new RepoObj
            {
                Name = "Test1",
                Strings = new[] { "a", "b" },
                Value = 10,
                Children = new[]
                {
                    new Child{ Dec = 10, Num = 30 },
                    new Child{ Dec = 12, Num = 356 },
                },
            };
            obj.Id = myRepo.Insert(obj);

            var other = new OtherObj
            {
                Name = "Other1",
            };
            other.Id = otherRepo.Insert(other);
            myRepo.Update(obj);

            var objs = myRepo.QueryAll().Entities;
            Assert.Single(objs);
            Assert.Equal("Test1", objs[0].Name);
        }

        [Fact]
        public void InterfaceMappingTest()
        {
            var mappings = new Mappings();
            mappings.Entity<IObj, StoredObj>()
                .WithConcreteFunc(i => i is StoredObj s ? s : StoredObj.From(i))
                .Infer(true)
                .Build();
            mappings.Entity<AObj>()
                .Infer(true)
                .Build();

            var repository = new Repository<IObj>(fixture.GetDb(), mappings);

            var aId = repository.Insert(new AObj() { Name = "aaa" });
            var bId = repository.Insert(new AObj() { Name = "bbb" });
            var objs = repository.QueryAll().Entities;
            Assert.Equal(2, objs.Count());
            Assert.Contains(objs, o => o.Name == "aaa");

            var obj = repository.FindById(aId).Value;
            Assert.Equal("aaa", obj.Name);
            Assert.Throws<AggregateException>(() => repository.Query(o => o.Name, "aaa").Entities);

            var sObj = repository.Query(new IObjFilter { PropertyName = "b", Value = "aaa" }).Entities.Single() as StoredObj;
            Assert.Equal(aId, sObj.Id);
        }

        [Fact]
        public void CustomFieldNameTest()
        {
            var mappings = new Mappings();
            mappings.Entity<AObj>()
                .Property(o => o.Name, "whatever")
                .Infer(true)
                .Build();

            var repository = new Repository<AObj>(fixture.GetDb(), mappings);

            repository.Insert(new AObj() { Name = "aaa" });
            repository.Insert(new AObj() { Name = "bbb" });
            var result = repository.Query(o => o.Name, "aaa");
            Assert.Single(result.Entities);
            Assert.True(result.Entities.Single(o => o.Name == "aaa") != null);

            repository.Query(new AObjFilter { PropertyName = "Name", Value = "aaa" });
            Assert.Single(result.Entities);
            Assert.True(result.Entities.Single(o => o.Name == "aaa") != null);
        }

        [Theory]
        [InlineData(1, 1, true)]
        [InlineData(1, 10, false)]
        [InlineData(1, 9, false)]
        [InlineData(1, 8, true)]
        [InlineData(2, 7, true)]
        [InlineData(2, 8, false)]
        [InlineData(2, 9, false)]
        [InlineData(null, 100, false)]
        [InlineData(10, 1, false)]
        [InlineData(10, 0, false)]
        [InlineData(0, null, false)]
        [InlineData(0, 10, false)]
        [InlineData(10, null, false)]
        public async void FilterTests(int? skip, int? take, bool hasMoreResults)
        {
            var mappings = new Mappings();
            mappings.Entity<AObj>()
                .Property(o => o.Name, "whatever")
                .Infer(true)
                .Build();

            var repository = new Repository<AObj>(fixture.GetDb(), mappings);

            var values = Enumerable.Range(0, 10).ToArray();
            foreach (var i in values)
                await repository.InsertAsync(new AObj() { Name = "a", Value = i });
            (await repository.QueryAllAsync()).Entities.Should().HaveCount(10);

            var result = await repository.QueryAsync(new AObjFilter
            {
                OrderBy = "Name",
                PropertyName = "Name",
                Value = "a",
                Skip = skip,
                Take = take
            });
            result
                .Entities
                .Select(e => e.Value)
                .Should()
                .BeEquivalentTo(values.Skip(skip ?? 0).Take(take ?? 100), opt => opt.WithStrictOrdering());
            result.HasMoreResults.Should().Be(hasMoreResults);
        }

        [Fact]
        public async Task QueryAsync_WithSkipAndTake_CursorWorks()
        {
            var mappings = new Mappings();
            mappings.Entity<AObj>()
                .Infer(true)
                .Build();

            var repository = new Repository<AObj>(fixture.GetDb(), mappings);

            var entities = Enumerable.Range(0, 10)
                .Select(index => new AObj() { Name = "a", Value = index })
                .ToArray();
            await repository.InsertAsync(entities);
            (await repository.QueryAllAsync()).Entities.Should().HaveCount(10);

            var filter = new AObjFilter { Take = 4 };
            var firstQueryResult = await repository.QueryAsync(filter);

            firstQueryResult.Entities.Should().HaveCount(4);
            firstQueryResult.HasMoreResults.Should().BeTrue();

            var secondQueryResult = await repository.QueryAsync(filter, firstQueryResult.NextCursor);

            secondQueryResult.Entities.Should().HaveCount(4);
            secondQueryResult.HasMoreResults.Should().BeTrue();

            var finalQueryResult = await repository.QueryAsync(filter, secondQueryResult.NextCursor);

            finalQueryResult.Entities.Should().HaveCount(2);
            finalQueryResult.HasMoreResults.Should().BeFalse();
        }

        [Fact]
        public void MaybePropertyTest()
        {
            var mappings = new Mappings();
            mappings.Entity<BObj>()
                .Infer(true)
                .Build();

            var repository = new Repository<BObj>(fixture.GetDb(), mappings);
            repository.Insert(new BObj { Name = "aaa".ToMaybe() });
            repository.Insert(new BObj { Name = "bbb".ToMaybe() });

            var result = repository.Query(o => o.Name, "aaa".ToMaybe());
            Assert.Single(result.Entities);
            Assert.True(result.Entities.Single(o => o.Name.Is("aaa")) != null);
        }

        [Fact]
        public void ExcludeFromIndexesFieldNameTest()
        {
            var mappings = new Mappings();
            mappings.Entity<LongTextPropertyObj>("LongText")
                .Property(o => o.LongText, excludeFromIndexes: true)
                .Infer(true)
                .Build();

            var db = fixture.GetDb();
            var repository = new Repository<LongTextPropertyObj>(db, mappings);
            var instance = new LongTextPropertyObj();
            repository.Insert(instance);

            var result = db.RunQuery(new Query("LongText"));
            var entity = result.Entities.Single();
            Assert.False(entity.Properties["smallText"].ExcludeFromIndexes);
            Assert.True(entity.Properties["longText"].ExcludeFromIndexes);
        }

        [Fact]
        public void RepositoryOperations_WithDecimalProperties_ShouldUseInvariantCulture()
        {
            var mappings = new Mappings();
            mappings.Entity<DecimalObj>()
                .Infer(true)
                .Build();

            var repository = new Repository<DecimalObj>(fixture.GetDb(), mappings);

            var decimalValue = 10.5m;

            var dotSeparatedCulture = CultureInfo.InvariantCulture;
            var commaSeparatedCulture = new CultureInfo("pt-BR");

            var task = new Task(() =>
            {
                // Ensure that culture change actually works
                Thread.CurrentThread.CurrentCulture = commaSeparatedCulture;
                var number = decimal.Parse("1,1");
                number.Should().Be(1.1m);

                // FindById with comma separated culture
                Thread.CurrentThread.CurrentCulture = dotSeparatedCulture;
                var id = repository.Insert(new DecimalObj { Data = decimalValue });

                Thread.CurrentThread.CurrentCulture = commaSeparatedCulture;
                repository.FindById(id).Value.Data.Should().Be(decimalValue);

                // Insert with comma separated culture
                Thread.CurrentThread.CurrentCulture = commaSeparatedCulture;
                id = repository.Insert(new DecimalObj { Data = decimalValue });

                Thread.CurrentThread.CurrentCulture = dotSeparatedCulture;
                repository.FindById(id).Value.Data.Should().Be(decimalValue);
            });
            task.Start();
            task.Wait();
        }

        [Fact]
        public async void DeleteById()
        {
            var mappings = new Mappings();
            mappings.Entity<RepoObj>().Infer(true).Build();

            var repo = new Repository<RepoObj>(fixture.GetDb(), mappings);
            var id = await repo.InsertAsync(new RepoObj { Name = "a", Value = 10 });

            // deleting invalid key doesn't cause any errors in Datastore
            await repo.DeleteAsync("12345");

            (await repo.QueryAllAsync()).Entities.Should().HaveCount(1);

            await repo.DeleteAsync(id);

            (await repo.QueryAllAsync()).Entities.Should().HaveCount(0);
        }

        private class DecimalObj
        {
            public string Id { get; set; }
            public decimal Data { get; set; }
        }

        private interface IObj
        {
            string Name { get; }
        }

        private class AObj : IObj
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int Value { get; set; }
        }

        private class StoredObj : IObj
        {
            public string A { get; set; }
            public string B { get; set; }

            public string Id { get; set; }
            public string Name { get => B; }

            internal static StoredObj From(IObj i)
            {
                return new StoredObj
                {
                    B = i.Name,
                    A = i.Name,
                };
            }
        }

        private class IObjFilter : IFilter<IObj>
        {
            public string PropertyName { get; set; }
            public string Value { get; set; }

            public void ApplyTo(Query query, IFieldResolver<IObj> resolver)
            {
                var name = resolver.FieldName(PropertyName);
                query.Filter = Filter.Equal(name, Value);
            }
        }

        private class AObjFilter : IFilter<AObj>
        {
            public string PropertyName { get; set; }
            public string Value { get; set; }
            public int? Take { get; set; }
            public int? Skip { get; set; }
            public string OrderBy { get; set; }
            public bool Ascending { get; set; } = true;

            public void ApplyTo(Query query, IFieldResolver<AObj> resolver)
            {
                if (PropertyName != null)
                {
                    var name = resolver.FieldName(PropertyName);
                    query.Filter = Filter.Equal(name, Value);
                }
                query.Limit = Take;
                query.Offset = Skip ?? 0;
                if (OrderBy != null)
                {
                    query.Order.Add(new PropertyOrder
                    {
                        Property = new PropertyReference { Name = resolver.FieldName(OrderBy) },
                        Direction = Ascending ? Direction.Ascending : Direction.Descending
                    });
                }
            }
        }

        private class BObj
        {
            public string Id { get; set; }
            public Maybe<string> Name { get; set; }
        }

        private class RepoObj
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string[] Strings { get; set; }
            public int Value { get; set; }
            public Child[] Children { get; set; }
        }

        private class Child
        {
            public decimal Dec { get; set; }
            public double Num { get; set; }
        }

        private class OtherObj
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        private class LongTextPropertyObj : IObj
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SmallText { get; set; }
            public string LongText { get; set; }
        }
    }
}
