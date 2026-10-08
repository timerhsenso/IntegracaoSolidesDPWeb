namespace SolidesDP.Fake.Tests;

public sealed class PagingTests : FakeTestBase
{
    private static readonly string[] PageProperties = ["content", "first", "last", "number", "numberOfElements", "size", "sort", "totalElements", "totalPages"];

    [Fact]
    public async Task Page_has_the_full_spring_shape()
    {
        var page = await GetJsonAsync("/companies");

        page.AsObject().Select(p => p.Key).Should().BeEquivalentTo(PageProperties);
        page["number"]!.GetValue<int>().Should().Be(0);
        page["size"]!.GetValue<int>().Should().Be(20); // tamanho padrao
        page["numberOfElements"]!.GetValue<int>().Should().Be(14);
        page["totalElements"]!.GetValue<int>().Should().Be(14);
        page["totalPages"]!.GetValue<int>().Should().Be(1);
        page["first"]!.GetValue<bool>().Should().BeTrue();
        page["last"]!.GetValue<bool>().Should().BeTrue();
        page["sort"].Should().NotBeNull();
    }

    [Theory]
    [InlineData("page={0}&size=5")]
    [InlineData("pageNumber={0}&pageSize=5")]
    public async Task Both_parameter_styles_paginate_zero_based(string template)
    {
        var first = await GetJsonAsync("/companies?" + string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0));
        var second = await GetJsonAsync("/companies?" + string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 1));
        var last = await GetJsonAsync("/companies?" + string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 2));

        first["content"]!.AsArray().Select(c => (long)c!["id"]!).Should().Equal(3001, 3002, 3003, 3004, 3005);
        second["content"]!.AsArray().Select(c => (long)c!["id"]!).Should().Equal(3006, 3007, 3008, 3009, 3010);
        last["content"]!.AsArray().Select(c => (long)c!["id"]!).Should().Equal(3011, 3012, 3013, 3014);
        first["totalPages"]!.GetValue<int>().Should().Be(3);
        first["first"]!.GetValue<bool>().Should().BeTrue();
        first["last"]!.GetValue<bool>().Should().BeFalse();
        second["number"]!.GetValue<int>().Should().Be(1);
        second["first"]!.GetValue<bool>().Should().BeFalse();
        last["numberOfElements"]!.GetValue<int>().Should().Be(4);
        last["last"]!.GetValue<bool>().Should().BeTrue();
        last["size"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_and_last()
    {
        var page = await GetJsonAsync("/companies?page=50&size=5");

        page["content"]!.AsArray().Should().BeEmpty();
        page["numberOfElements"]!.GetValue<int>().Should().Be(0);
        page["last"]!.GetValue<bool>().Should().BeTrue();
        page["totalElements"]!.GetValue<int>().Should().Be(14);
    }

    [Fact]
    public async Task An_empty_collection_has_zero_pages_and_is_first_and_last()
    {
        var page = await GetJsonAsync("/job-role/find-all");

        page["totalElements"]!.GetValue<int>().Should().Be(0);
        page["totalPages"]!.GetValue<int>().Should().Be(0);
        page["first"]!.GetValue<bool>().Should().BeTrue();
        page["last"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("size=5000", 1000)]
    [InlineData("size=0", 20)]
    [InlineData("size=-3", 20)]
    [InlineData("pageSize=abc", 20)]
    [InlineData("size=1000", 1000)]
    public async Task Size_is_capped_at_1000_and_falls_back_to_20(string query, int expectedSize)
    {
        var page = await GetJsonAsync("/companies?" + query);

        page["size"]!.GetValue<int>().Should().Be(expectedSize);
    }

    [Fact]
    public async Task Negative_or_invalid_page_numbers_fall_back_to_the_first_page_and_offset_is_ignored()
    {
        (await GetJsonAsync("/companies?page=-1&size=5"))["number"]!.GetValue<int>().Should().Be(0);
        (await GetJsonAsync("/companies?page=abc&size=5"))["number"]!.GetValue<int>().Should().Be(0);
        (await GetJsonAsync("/companies?offset=10&size=5"))["number"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Pagination_also_applies_inside_the_punch_rule_envelope()
    {
        var response = await GetJsonAsync("/v2/punch-rule?page=1&size=1");

        response["item"]!["number"]!.GetValue<int>().Should().Be(1);
        response["item"]!["content"]!.AsArray().Select(r => (long)r!["id"]!).Should().Equal(2002);
        response["item"]!["totalPages"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Every_find_all_endpoint_paginates()
    {
        await CreateEmployeeAsync("E-1");
        await CreateEmployeeAsync("E-2");
        await CreateEmployeeAsync("E-3");

        var page = await GetJsonAsync("/employee/find-all?page=1&size=2");

        page["content"]!.AsArray().Select(e => (string?)e!["externalId"]).Should().Equal("E-3");
        page["totalElements"]!.GetValue<int>().Should().Be(3);
        (await GetJsonAsync("/adjustment-reason/find-all?pageNumber=1&pageSize=3"))["content"]!.AsArray().Should().ContainSingle();
        (await GetJsonAsync("/work-schedule?size=1"))["totalPages"]!.GetValue<int>().Should().Be(2);
    }
}
