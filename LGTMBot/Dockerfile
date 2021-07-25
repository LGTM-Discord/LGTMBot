FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["LGTMBot/LGTMBot.csproj", "LGTMBot/"]
RUN dotnet restore "LGTMBot/LGTMBot.csproj"
COPY . .
WORKDIR "/src/LGTMBot"
RUN dotnet build "LGTMBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "LGTMBot.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LGTMBot.dll"]
