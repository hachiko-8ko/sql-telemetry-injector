# SQL Telemetry Injector

Have you ever needed to troubleshoot a complex stored procedure, but couldn't step through the code because you were using Azure SQL, or the SQL debugger was firewalled off, or you weren't made part of the sysadmin group? Tough, right? You have to go back to the old debugging style of adding PRINT statements all over the place. This is a first stab at automating that.

This little console application (coded by a new member of the Slop Company because the previous agents started throwing errors) adds insert statements after every _n_ statements (configurable) to write the statement and a watchlist of all variables to a global temp table. Drop the SP scripts into the input folder, run the program, and read the output from the output folder. See _appsettings.json_ to see the options. It uses Microsoft.SqlServer.TransactSql.ScriptDom to parse the T-SQL code, so should be more robust than a regex-based approach.

With this design, I didn't think it was safe to do this and leave it as a SP, as a general rule (it would throw if the table wasn't created), so the SP is converted into a flat script. This does mean that it won't work for you if you have nested procedures, without some manual editing. The current idea is that you're running the script in SSMS, not replacing the SP that sits behind your website or whatever. If you look at the code it produces, it's not something you want sitting behind a production site but just something for support technicians who are pulling their hair out.

There is no special handling for very large variables, binaries, CLR types, etc. There is an ExcludedVariables option, which should work in a pinch. It'd be a pretty tough problem to solve.

Standard caveat: T-SQL is a big language, and there are a lot of edge cases. The commit this line is coming in with is when I realized that writing to a table would mess up common special variables like @@ROWCOUNT and SCOPE_IDENTITY, so I handled those two. And just those two. I am ignoring much of the SQL Server feature set and focusing on the stuff that was common ten years ago (for reasons).

## How to use

The program is configured by appsettings.json. They're pretty self-explanatory, but I'm not entirely sure how useful all will be. Some settings, like the variable filters or the transaction wrapper, make more sense for a single script, not batch processing a directory, but they're easy to set.

```
{
    "InputDirectory": "./input",
    "OutputDirectory": "./output",
    "ArchiveDirectory": "./archive",
    "StatementFrequency": 4,
    "OnlySqlFiles": true,
    "ArchiveOriginalFile": true,
    "IncludeTransactionWrapper": true,
    "TelemetryTableName": "##T9_Telemetry",
    "ExcludedVariables": [],
    "WatchedVariables": []
}
```

After you've run the console program to generate your script, open it in SSMS, set values in the first block of variables (SP parameters are converted into regular variable definitions), and run the script. Then you can select from or search the temp table.

```
SELECT  *
FROM    ##T9_Telemetry
ORDER BY 1
```

The procedure attempts to surround (by default) statements with BEGIN TRAN/ROLLBACK so live data doesn't get modified when you're troubleshooting.

## The Slop Company

The Slop Company is a band of AI agents building applications. Everything released by Slop Company is vibe coded using Generative AI ... not because it's necessary or efficient or a good idea, but because it's there. 100% of all slop code is reviewed and edited and refactored by me.

_Aside from a few edge cases I spotted, the AI seemed to handle this one fairly well on its own. After all, it's little more than a glorified search & replace._
