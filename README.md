**I removed the program files I uploaded as I had accidentally left some personal info in the file paths hard-coded into the files. If requested, I can clean them and re-upload them.**

The purpose of these 2 programs is to grab data from the NHL API. I specifically retrieved the data on all playoff games from the 1963-1993 seasons, and the 1993-2025 seasons where a US team faced a Canada team.

My intent was to get the actual backing data to verify the claims made by u/ccrypt524 on Reddit, who claims that since Gary Bettman took over the NHL, that Canadian teams have gotten disproportionately more penalties than the US teams when in a direct match-up. 

Methodology: I used a simple C# script to retrieve game data from the public NHL API. It ran on playoff games for each season from the 1963-1964 season, all the way through the 2024-2025 season, checked if it was a US and a Canada team playing, and then wrote the PIM (penalty minute)
stats plus the country to a JSON file for each season. I then used another script to read the JSON files and convert it to a CSV file. I then imported that into a Google Sheets where I used a query function to grab the country and TeamPIM stat, and added them all together by country. 
I then divided the US's PIM total by Canada's PIM total to get the differential. 

Result: A PIM differential of 1 would mean that both the US and Canada had equal penalty minutes. A <1 would indicate that the US teams had less penalty minutes than the Canada teams. 
As you can see from the output:
Pre-93 - PIM Differential of ~1.013, which means that the US got 101.3% of the penalty minutes that Canada did. 
Post-93 - PIM Differential of ~.935, which means the US got 93.5% of the penalty minutes that Canada did.

Conclusion:
As the official NHL data shows, the US teams, on average, are getting ~6.5% fewer penalties than the Canadian teams post-Bettman. I will note that I was not able to reasonably account for some outlier data like teams that switched countries.

Note: I am not a professional data analyst or statistician, just a hobby programmer who loves hockey. While I did my best to check my work, it is always possible that I made a mistake. I have included the exported JSONs and CSV files if anyone else would like to check my work, in fact I
would appreciate it as it has been 8+ years since my statistics class in University and am quite rusty. It is not my intention to disparage anyone, or to bring negative attention to anyone. I operated with the sole intention of bringing the data to confirm the information that was being 
presented as fact. I, like many other commenters on the original post, had our doubts as the OP would not provide their data source and only had some graphs supposedly proving their point. 
