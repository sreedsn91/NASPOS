# API image: build with the SDK, run on the slim ASP.NET runtime as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Kshethra.sln ./
COPY Kshethra.Domain/Kshethra.Domain.csproj Kshethra.Domain/
COPY Kshethra.Application/Kshethra.Application.csproj Kshethra.Application/
COPY Kshethra.Infrastructure/Kshethra.Infrastructure.csproj Kshethra.Infrastructure/
COPY Kshethra.Api/Kshethra.Api.csproj Kshethra.Api/
COPY Kshethra.Tests/Kshethra.Tests.csproj Kshethra.Tests/
RUN dotnet restore Kshethra.Api/Kshethra.Api.csproj
COPY . .
RUN dotnet publish Kshethra.Api/Kshethra.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
# QuestPDF needs fontconfig/freetype on Linux.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Kshethra.Api.dll"]
