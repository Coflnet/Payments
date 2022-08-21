VERSION=0.8.0

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5020/swagger/v1/swagger.json \
-g csharp-netcore \
-o /local/out --additional-properties=packageName=Coflnet.Payments.Client,packageVersion=$VERSION,licenseId=MIT

cd out
sed -i 's/GIT_USER_ID/Coflnet/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj
sed -i 's/GIT_REPO_ID/Payments/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj
sed -i 's/>OpenAPI/>Coflnet/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj

FlagFile="src/Coflnet.Payments.Client/Model/RuleFlags.cs"
sed -i 's/= 1/= 0/g' $FlagFile
sed -i 's/= 2/= 1/g' $FlagFile
sed -i 's/= 3/= 2/g' $FlagFile
sed -i 's/= 4/= 4/g' $FlagFile
sed -i 's/= 5/= 8/g' $FlagFile
sed -i 's/= 6/= 16/g' $FlagFile
sed -i 's/= 7/= 32/g' $FlagFile
sed -i 's/    public enum/    [Flags]\n    public enum/g' $FlagFile

dotnet pack
cp src/Coflnet.Payments.Client/bin/Debug/Coflnet.Payments.Client.*.nupkg ..
